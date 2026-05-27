using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BetterExplorer.Controls;

/// <summary>
/// Locates and invokes TeraCopy v3+ via its command-line interface.
/// </summary>
internal static class TeraCopyHelper
{
    // Cached path once found, or string.Empty when confirmed absent.
    private static string? _cachedPath;

    /// <summary>
    /// Returns the full path to TeraCopy.exe, or <see langword="null"/> if it is not installed.
    /// The result is cached after the first call.
    /// </summary>
    public static string? FindExe()
    {
        if (_cachedPath is not null)
            return _cachedPath.Length == 0 ? null : _cachedPath;

        // 1. App Paths registry key (preferred – works for per-machine and per-user installs).
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appKey  = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\TeraCopy.exe");
                    if (appKey?.GetValue(null) is string rawPath)
                    {
                        // Registry values are sometimes surrounded by quotes — strip them.
                        var path = rawPath.Trim().Trim('"');
                        if (File.Exists(path))
                            return _cachedPath = path;
                    }
                }
                catch { /* registry not accessible — skip */ }
            }
        }

        // 2. Well-known install directories.
        //    Use both the SpecialFolder enum and the environment variable; in some
        //    packaged-app contexts one can differ from the other.
        var pfDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramFiles")     ?? string.Empty,
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var pf in pfDirs)
        {
            if (string.IsNullOrEmpty(pf)) continue;

            var tcDir = Path.Combine(pf, "TeraCopy");
            if (!Directory.Exists(tcDir)) continue;

            // Try the canonical name first, then scan for any TeraCopy*.exe in the folder.
            var canonical = Path.Combine(tcDir, "TeraCopy.exe");
            if (File.Exists(canonical))
                return _cachedPath = canonical;

            var found = Directory.GetFiles(tcDir, "TeraCopy*.exe", SearchOption.TopDirectoryOnly)
                                 .FirstOrDefault();
            if (found is not null)
                return _cachedPath = found;
        }

        // Not found — cache the negative result.
        _cachedPath = string.Empty;
        return null;
    }

    /// <summary>Returns <see langword="true"/> when TeraCopy is installed on this machine.</summary>
    public static bool IsAvailable() => FindExe() is not null;

    /// <summary>
    /// Invalidates the cached exe path so the next call to <see cref="FindExe"/> rescans.
    /// Useful if TeraCopy is installed/uninstalled while the app is running.
    /// </summary>
    public static void InvalidateCache() => _cachedPath = null;

    /// <summary>
    /// Launches TeraCopy to copy or move <paramref name="sourcePaths"/> into
    /// <paramref name="destFolder"/>. The TeraCopy window opens independently;
    /// this method returns immediately.
    /// </summary>
    /// <param name="sourcePaths">Full paths of files/folders to operate on.</param>
    /// <param name="destFolder">Destination folder path.</param>
    /// <param name="move"><see langword="true"/> for Move; <see langword="false"/> for Copy.</param>
    public static void Invoke(IReadOnlyList<string> sourcePaths, string destFolder, bool move)
    {
        var exe = FindExe()
            ?? throw new InvalidOperationException("TeraCopy is not installed or could not be found.");

        // TeraCopy v3 CLI:
        //   Single source  : TeraCopy.exe Copy|Move "src"    "dest\"
        //   Multiple sources: TeraCopy.exe Copy|Move *"<tmpfile>" "dest\"
        //
        // The * prefix tells TeraCopy the argument is a list-file (one path per
        // line). Passing multiple quoted paths inline is NOT supported — only
        // the first source is read when no list-file prefix is present.

        var listFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(listFile, sourcePaths.Select(p => p.TrimEnd('\\', '/')));

            // Destination must end with a backslash so TeraCopy treats it as a directory.
            var dest = destFolder.TrimEnd('\\', '/') + '\\';

            var args = new StringBuilder();
            args.Append(move ? "Move" : "Copy");
            args.Append(" *\"");
            args.Append(listFile);
            args.Append("\" \"");
            args.Append(dest);
            args.Append('"');

            Process.Start(new ProcessStartInfo(exe, args.ToString())
            {
                UseShellExecute = false,
            });
            // Note: the temp file must remain on disk until TeraCopy reads it.
            // TeraCopy reads it synchronously before its window appears, so a
            // small delay then delete is sufficient. We register a one-shot
            // cleanup via a background task rather than blocking the UI thread.
            _ = Task.Delay(TimeSpan.FromSeconds(5))
                    .ContinueWith(_ => { try { File.Delete(listFile); } catch { } },
                                  TaskScheduler.Default);
        }
        catch
        {
            // If anything went wrong before the process started, clean up now.
            try { File.Delete(listFile); } catch { }
            throw;
        }
    }
}
