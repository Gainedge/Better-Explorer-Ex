using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Win32;

namespace BetterExplorer.ShellApi.Interop;

/// <summary>
/// Integrates with the voidtools Everything search engine via its SDK DLL
/// (Everything64.dll / Everything32.dll), loaded dynamically at runtime so
/// the app continues to work on machines where Everything is not installed.
/// </summary>
public static class EverythingSearch {
  // ── Request flag constants (from official Everything SDK) ─────────────────
  private const uint EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
  private const uint EVERYTHING_REQUEST_PATH = 0x00000002;
  private const uint EVERYTHING_REQUEST_SIZE = 0x00000010;  // NOTE: 0x10 not 0x20
  private const uint EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
  private const uint EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;  // NOTE: 0x100 not 0x10

  // Win32 file attribute bits
  private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
  private const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
  private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

  // ── Delegate types that match the DLL exports exactly ─────────────────────
  // Delegates that carry string/StringBuilder parameters MUST declare CharSet.Unicode
  // so the runtime marshals them as LPWSTR.  Without this attribute the default is
  // CharSet.Ansi (LPSTR), which turns e.g. "test" into garbled wide characters and
  // causes Everything_SetSearchW to receive a query that matches nothing.
  [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
  private delegate void SetSearchWDelegate(string s);
  private delegate void SetRequestFlagsDelegate(uint flags);
  private delegate void SetMaxDelegate(uint max);
  private delegate bool QueryWDelegate(bool wait);
  private delegate uint GetNumResultsDelegate();
  // GetResultFullPathNameW writes UTF-16 into the buffer; must also be Unicode.
  [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
  private delegate uint GetResultFullPathNameWDelegate(uint idx, StringBuilder buf, uint nMaxCount);
  private delegate uint GetResultAttributesDelegate(uint idx);
  private delegate bool GetResultSizeDelegate(uint idx, out long lpFileSize);
  private delegate bool GetResultDateModifiedDelegate(uint idx, out long lpFileTime);

  // ── Resolved delegates (null until TryLoad succeeds) ─────────────────────
  private static SetSearchWDelegate? _setSearch;
  private static SetRequestFlagsDelegate? _setRequestFlags;
  private static SetMaxDelegate? _setMax;
  private static QueryWDelegate? _queryW;
  private static GetNumResultsDelegate? _getNumResults;
  private static GetResultFullPathNameWDelegate? _getResultFullPathName;
  private static GetResultAttributesDelegate? _getResultAttributes;
  private static GetResultSizeDelegate? _getResultSize;
  private static GetResultDateModifiedDelegate? _getResultDateModified;

  // ── One-time load state ───────────────────────────────────────────────────
  private static bool _loaded;
  private static bool _available;
  private static readonly object _loadLock = new();

  // ─────────────────────────────────────────────────────────────────────────
  // Public API
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Returns <c>true</c> when Everything is installed and its SDK DLL loaded
  /// successfully.  Thread-safe; cached after the first call.
  /// </summary>
  public static bool IsAvailable() {
    if (_loaded)
      return _available;
    lock (_loadLock) {
      if (_loaded)
        return _available;
      _available = TryLoad();
      _loaded = true;
    }
    return _available;
  }

  /// <summary>
  /// Clears the cached availability result so the next call to
  /// <see cref="IsAvailable"/> re-probes the disk/registry.
  /// </summary>
  public static void InvalidateCache() {
    lock (_loadLock) {
      _loaded = false;
      _available = false;
    }
  }

  /// <summary>
  /// Runs <paramref name="query"/> via Everything, scoped to
  /// <paramref name="folderPath"/> and all its descendants, and streams
  /// matching <see cref="ShellItem"/> objects into the returned
  /// <see cref="ChannelReader{T}"/>.  The work is done on a dedicated
  /// background STA thread, mirroring the pattern used by
  /// <see cref="NativeShell.SearchFolderStreamAsync"/>.
  /// </summary>
  public static ChannelReader<ShellItem> SearchFolderStreamAsync(
      string folderPath, string query, CancellationToken ct) {
    var channel = Channel.CreateUnbounded<ShellItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    if (!IsAvailable()
        || string.IsNullOrWhiteSpace(folderPath)
        || string.IsNullOrWhiteSpace(query)) {
      channel.Writer.Complete();
      return channel.Reader;
    }

    // Folder prefix used for client-side scoping (with trailing backslash).
    string folderPrefix = folderPath.TrimEnd('\\', '/') + '\\';

    // Send just the user's query to Everything — no path: modifier.
    // The path: modifier in Everything's query language uses substring matching
    // and has quoting edge-cases with trailing backslashes; it is much simpler
    // and more reliable to let Everything search broadly and scope results
    // client-side via a StartsWith check on folderPrefix.
    string searchStr = query;

    var thread = new Thread(() => {
      try {
        ct.ThrowIfCancellationRequested();
        RunQuery(searchStr, folderPrefix, ct, channel.Writer);
        channel.Writer.Complete();
      } catch (OperationCanceledException) {
        channel.Writer.Complete();
      } catch (Exception ex) {
        Debug.WriteLine($"[EverythingSearch] {ex.GetType().Name}: {ex.Message}");
        channel.Writer.Complete();
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();

    return channel.Reader;
  }

  /// <summary>
  /// Runs <paramref name="query"/> via Everything with no folder scoping —
  /// searching the entire index (all drives).  Results are streamed into the
  /// returned <see cref="ChannelReader{T}"/> on a dedicated STA thread.
  /// </summary>
  public static ChannelReader<ShellItem> SearchGlobalStreamAsync(
      string query, CancellationToken ct) {
    var channel = Channel.CreateUnbounded<ShellItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    if (!IsAvailable() || string.IsNullOrWhiteSpace(query)) {
      channel.Writer.Complete();
      return channel.Reader;
    }

    string searchStr = query;

    var thread = new Thread(() => {
      try {
        ct.ThrowIfCancellationRequested();
        RunQuery(searchStr, null, ct, channel.Writer);
        channel.Writer.Complete();
      } catch (OperationCanceledException) {
        channel.Writer.Complete();
      } catch (Exception ex) {
        Debug.WriteLine($"[EverythingSearch] {ex.GetType().Name}: {ex.Message}");
        channel.Writer.Complete();
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();

    return channel.Reader;
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Private helpers
  // ─────────────────────────────────────────────────────────────────────────

  private static void RunQuery(
      string searchStr,
      string? folderPrefix,
      CancellationToken ct,
      ChannelWriter<ShellItem> writer) {
    // Mirror the official SDK example pattern exactly:
    //   SetSearchW → SetRequestFlags → SetMax → QueryW → enumerate results
    _setSearch!(searchStr);
    _setRequestFlags!(
        EVERYTHING_REQUEST_FILE_NAME |
        EVERYTHING_REQUEST_PATH |
        EVERYTHING_REQUEST_SIZE |
        EVERYTHING_REQUEST_DATE_MODIFIED |
        EVERYTHING_REQUEST_ATTRIBUTES);
    _setMax!(5_000);

    bool ok = _queryW!(true);
    Debug.WriteLine($"[EverythingSearch] QueryW={ok}  last_error={GetLastError()}");
    if (!ok)
      return;

    ct.ThrowIfCancellationRequested();

    uint count = _getNumResults!();
    Debug.WriteLine($"[EverythingSearch] count={count}  prefix={folderPrefix ?? "(global)"}");

    var buf = new StringBuilder(4_096);

    for (uint i = 0; i < count; i++) {
      ct.ThrowIfCancellationRequested();

      buf.Clear();
      _getResultFullPathName!(i, buf, (uint)buf.Capacity);
      string fullPath = buf.ToString();
      if (string.IsNullOrEmpty(fullPath))
        continue;

      // Scope: only emit results that live under folderPrefix (null = no scoping).
      if (folderPrefix is not null
          && !fullPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
        continue;

      uint attrs = _getResultAttributes!(i);
      bool isDir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
      bool hidden = (attrs & FILE_ATTRIBUTE_HIDDEN) != 0;
      bool isLinkItem = (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
      bool isShortcut = NativeShell.IsShortcutPath(fullPath);
      bool isArchive = NativeShell.IsArchivePath(fullPath);

      long sizeBytes = 0;
      if (!isDir)
        _getResultSize!(i, out sizeBytes);

      DateTime dateModified = DateTime.Now;
      if (_getResultDateModified!(i, out long ft) && ft > 0) {
        try { dateModified = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { /* leave as Now */ }
      }

      string ext = isDir ? string.Empty : Path.GetExtension(fullPath);

      // IShellItem2.GetString(PKEY_ItemTypeText) gives the shell's own friendly
      // type string ("PNG File", "Text Document", etc.) — better than a raw
      // extension lookup.  Fall back to extension lookup if the COM call fails.
      var typeText = NativeShell.GetItemTypeText(fullPath);

      writer.TryWrite(new ShellItem {
        Name = Path.GetFileName(fullPath),
        DisplayName = Path.GetFileName(fullPath),
        FullPath = fullPath,
        IsFolder = isDir,
        IsShortcut = isShortcut,
        IsLinkItem = isLinkItem,
        IsArchive = isArchive,
        SizeBytes = sizeBytes,
        Size = isDir ? string.Empty : FormatSize(sizeBytes),
        DateModified = dateModified,
        ItemType = isDir
                                   ? "File folder"
                                   : (string.IsNullOrEmpty(ext)
                                         ? "File"
                                         : string.IsNullOrEmpty(typeText)
                                         ? ext.TrimStart('.').ToUpperInvariant() + " File" 
                                         : typeText),
        IsHidden = hidden,
      });
    }
  }

  // ── DLL loading ───────────────────────────────────────────────────────────

  // Delegate for Everything_GetLastError (only used for debug logging).
  private delegate uint GetLastErrorDelegate();
  private static GetLastErrorDelegate? _getLastError;

  private static uint GetLastError() => _getLastError?.Invoke() ?? 0;

  private static bool TryLoad() {
    string? dllPath = FindEverythingDll();
    if (dllPath is null)
      return false;

    Debug.WriteLine($"[EverythingSearch] Loading DLL from: {dllPath}");

    IntPtr hModule;
    try { hModule = NativeLibrary.Load(dllPath); } catch (Exception ex) { Debug.WriteLine($"[EverythingSearch] NativeLibrary.Load failed: {ex.Message}"); return false; }

    try {
      _setSearch = Bind<SetSearchWDelegate>(hModule, "Everything_SetSearchW");
      _setRequestFlags = Bind<SetRequestFlagsDelegate>(hModule, "Everything_SetRequestFlags");
      _setMax = Bind<SetMaxDelegate>(hModule, "Everything_SetMax");
      _queryW = Bind<QueryWDelegate>(hModule, "Everything_QueryW");
      _getNumResults = Bind<GetNumResultsDelegate>(hModule, "Everything_GetNumResults");
      _getResultFullPathName = Bind<GetResultFullPathNameWDelegate>(hModule, "Everything_GetResultFullPathNameW");
      _getResultAttributes = Bind<GetResultAttributesDelegate>(hModule, "Everything_GetResultAttributes");
      _getResultSize = Bind<GetResultSizeDelegate>(hModule, "Everything_GetResultSize");
      _getResultDateModified = Bind<GetResultDateModifiedDelegate>(hModule, "Everything_GetResultDateModified");

      // Optional — only used for debug diagnostics.
      if (NativeLibrary.TryGetExport(hModule, "Everything_GetLastError", out var p))
        _getLastError = Marshal.GetDelegateForFunctionPointer<GetLastErrorDelegate>(p);

      Debug.WriteLine("[EverythingSearch] DLL loaded and all exports resolved.");
      return true;
    } catch (Exception ex) {
      Debug.WriteLine($"[EverythingSearch] Export binding failed: {ex.Message}");
      return false;
    }
  }

  private static T Bind<T>(IntPtr hModule, string name) where T : Delegate =>
      Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(hModule, name));

  private static string? FindEverythingDll() {
    var dirs = new List<string>();

    // 1. Registry — most reliable source (written by Everything's installer).
    string[] regKeys = [@"SOFTWARE\voidtools", @"SOFTWARE\voidtools\Everything"];
    foreach (string regKey in regKeys) {
      try {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser }) {
          using var key = hive.OpenSubKey(regKey, writable: false);
          if (key is null)
            continue;

          if (key.GetValue("InstallLocation") is string loc1 && !string.IsNullOrEmpty(loc1))
            dirs.Add(loc1);

          using var child = key.OpenSubKey("Everything", writable: false);
          if (child?.GetValue("InstallLocation") is string loc2 && !string.IsNullOrEmpty(loc2))
            dirs.Add(loc2);
        }
      } catch { }
    }

    // 2. Running Everything.exe process path.
    try {
      foreach (var p in Process.GetProcessesByName("Everything")) {
        try {
          if (Path.GetDirectoryName(p.MainModule?.FileName) is string d)
            dirs.Add(d);
        } catch { }
      }
    } catch { }

    // 3. Hard-coded well-known install paths (covers GetFolderPath returning empty
    //    inside a packaged app sandbox).
    dirs.Add(@"C:\Program Files\Everything");
    dirs.Add(@"C:\Program Files (x86)\Everything");
    try {
      string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
      string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
      if (!string.IsNullOrEmpty(pf))
        dirs.Add(Path.Combine(pf, "Everything"));
      if (!string.IsNullOrEmpty(pf86))
        dirs.Add(Path.Combine(pf86, "Everything"));
    } catch { }

    foreach (string dir in dirs) {
      if (string.IsNullOrWhiteSpace(dir))
        continue;
      foreach (string dll in new[] { "Everything64.dll", "Everything32.dll" }) {
        string candidate = Path.Combine(dir, dll);
        if (File.Exists(candidate))
          return candidate;
      }
    }

    return null;
  }

  private static string FormatSize(long bytes) {
    if (bytes < 1_024)
      return $"{bytes} B";
    if (bytes < 1_024 * 1_024)
      return $"{bytes / 1_024.0:F1} KB";
    if (bytes < 1_024L * 1_024 * 1_024)
      return $"{bytes / (1_024.0 * 1_024):F1} MB";
    return $"{bytes / (1_024.0 * 1_024 * 1_024):F2} GB";
  }
}
