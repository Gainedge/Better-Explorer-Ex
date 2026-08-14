using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BetterExplorer.ShellApi.Interop;

// ── Persistent STA worker thread ──────────────────────────────────────────────

/// <summary>
/// Keeps a single STA thread alive with a blocking work queue.
/// All COM objects created via <see cref="Run"/> must also be called via <see cref="Run"/>.
/// </summary>
internal sealed class StaWorker : IDisposable {
  private readonly BlockingCollection<Action> _queue = new();
  private readonly Thread _thread;

  public StaWorker() {
    _thread = new Thread(() => {
      foreach (var action in _queue.GetConsumingEnumerable())
        try { action(); } catch { /* individual items handle their own exceptions */ }
    });
    _thread.SetApartmentState(ApartmentState.STA);
    _thread.IsBackground = true;
    _thread.Start();
  }

  /// <summary>Posts work to the STA thread and returns a Task that completes when done.</summary>
  public Task Run(Action action) {
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _queue.Add(() => {
      try   { action(); tcs.SetResult(); }
      catch (Exception ex) { tcs.SetException(ex); }
    });
    return tcs.Task;
  }

  /// <summary>Posts work to the STA thread and returns a Task&lt;T&gt; with the result.</summary>
  public Task<T> Run<T>(Func<T> func) {
    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    _queue.Add(() => {
      try   { tcs.SetResult(func()); }
      catch (Exception ex) { tcs.SetException(ex); }
    });
    return tcs.Task;
  }

  public void Dispose() => _queue.CompleteAdding();
}

// ── Data model ────────────────────────────────────────────────────────────────

/// <summary>
/// A single item in a shell context menu, produced by
/// <see cref="ShellContextMenuSession.Items"/>.
/// </summary>
public sealed record ShellContextMenuItem(
    /// <summary>Win32 menu item ID (absolute, not an offset).</summary>
    uint   Id,
    /// <summary>Display label from the HMENU string.</summary>
    string Label,
    /// <summary>
    /// Canonical shell verb string from <c>GetCommandString(GCS_VERBW)</c>,
    /// or empty when the verb is unavailable.
    /// </summary>
    string Verb,
    bool   IsSeparator,
    bool   IsDisabled,
    bool   HasSubmenu,
    /// <summary>Recursively built child items when <see cref="HasSubmenu"/> is true.</summary>
    IReadOnlyList<ShellContextMenuItem>? SubItems,
    /// <summary>
    /// Raw premultiplied BGRA pixel data extracted from the HMENU bitmap on the STA thread,
    /// or <see langword="null"/> when no bitmap is available. Width == Height == <see cref="IconSize"/>.
    /// </summary>
    byte[]? IconPixels,
    int IconW,
    int IconH);

// ── Session (owns unmanaged resources) ────────────────────────────────────────

/// <summary>
/// Encapsulates an active <c>IContextMenu</c> (+ optional <c>IContextMenu2/3</c>)
/// together with the <c>HMENU</c> populated by <c>QueryContextMenu</c>.
/// Dispose when the flyout closes to release COM objects and the menu handle.
/// </summary>
public sealed class ShellContextMenuSession : IDisposable {
  private NativeShell.IContextMenuCM?  _cm;
  private NativeShell.IContextMenu2CM? _cm2;
  private NativeShell.IContextMenu3CM? _cm3;
  private IntPtr _hMenuRoot;
  private bool   _disposed;
  // The STA worker that owns (created) all the COM objects above.
  // All COM calls must be posted back here.
  private readonly StaWorker _sta;
  // When false the STA is shared and must NOT be disposed by this session.
  private readonly bool _ownsSta;

  internal const uint CmdFirst = 1;
  internal const uint CmdLast  = 0x7FFF;

  internal ShellContextMenuSession(
      NativeShell.IContextMenuCM  cm,
      NativeShell.IContextMenu2CM? cm2,
      NativeShell.IContextMenu3CM? cm3,
      IntPtr hMenuRoot,
      StaWorker sta,
      bool ownsSta = true) {
    _cm        = cm;
    _cm2       = cm2;
    _cm3       = cm3;
    _hMenuRoot = hMenuRoot;
    _sta       = sta;
    _ownsSta   = ownsSta;
  }

  // ── Public surface ────────────────────────────────────────────────────────

  /// <summary>All top-level items from the shell context menu.</summary>
  public IReadOnlyList<ShellContextMenuItem> Items { get; private set; } =
      Array.Empty<ShellContextMenuItem>();

  /// <summary>True if the session has <c>IContextMenu3</c> support.</summary>
  public bool HasContextMenu3 => _cm3 is not null;

  /// <summary>True if the session has at least <c>IContextMenu2</c> support.</summary>
  public bool HasContextMenu2 => _cm2 is not null || _cm3 is not null;

  // ── Internal build ────────────────────────────────────────────────────────

  internal void BuildItems() => Items = BuildFromMenu(_hMenuRoot);

  // WM_INITMENUPOPUP — triggers lazy submenu population in IContextMenu2/3 handlers.
  private const uint WM_INITMENUPOPUP = 0x0117;

  // Well-known shell menu labels → canonical verbs.
  // Avoids a GetCommandString COM round-trip for the items that appear on every menu.
  private static readonly Dictionary<string, string> _labelToVerb =
      new(StringComparer.OrdinalIgnoreCase) {
    ["Open"]               = "open",
    ["Open with"]          = "openas",
    ["Open With"]          = "openas",
    ["Explore"]            = "explore",
    ["Find"]               = "find",
    ["Cut"]                = "cut",
    ["Copy"]               = "copy",
    ["Paste"]              = "paste",
    ["Paste shortcut"]     = "pastelink",
    ["Create shortcut"]    = "link",
    ["Delete"]             = "delete",
    ["Rename"]             = "rename",
    ["Properties"]         = "properties",
    ["Share"]              = "share",
    ["Print"]              = "print",
    ["Run as administrator"] = "runas",
    ["Pin to Start"]       = "pin",
    ["Unpin from Start"]   = "unpin",
    ["Send to"]            = "sendto",
    ["New"]                = "new",
  };

  private IReadOnlyList<ShellContextMenuItem> BuildFromMenu(IntPtr hMenu) {
    var list  = new List<ShellContextMenuItem>();
    int count = NativeShell.GetMenuItemCount(hMenu);
    const int BufChars  = 512;
    const int VerbChars = 128;

    // Allocate both buffers once outside the loop to avoid N alloc/free pairs.
    IntPtr labelBuf = Marshal.AllocHGlobal(BufChars  * 2);
    IntPtr verbBuf  = Marshal.AllocHGlobal(VerbChars * 2);
    try {
    for (int i = 0; i < count; i++) {
        Marshal.WriteInt16(labelBuf, 0);
        var mii = new NativeShell.MENUITEMINFOW {
          cbSize     = (uint)Marshal.SizeOf<NativeShell.MENUITEMINFOW>(),
          fMask      = NativeShell.MENUITEMINFOW.MIIM_ID
                     | NativeShell.MENUITEMINFOW.MIIM_FTYPE
                     | NativeShell.MENUITEMINFOW.MIIM_STATE
                     | NativeShell.MENUITEMINFOW.MIIM_SUBMENU
                     | NativeShell.MENUITEMINFOW.MIIM_STRING
                     | NativeShell.MENUITEMINFOW.MIIM_BITMAP,
          dwTypeData = labelBuf,
          cch        = (uint)BufChars,
        };

        if (!NativeShell.GetMenuItemInfoW(hMenu, (uint)i, fByPosition: true, ref mii))
          continue;

        bool isSep    = (mii.fType  & NativeShell.MENUITEMINFOW.MFT_SEPARATOR) != 0;
        bool disabled = (mii.fState & NativeShell.MENUITEMINFOW.MFS_DISABLED)  != 0;
        bool hasSub   = mii.hSubMenu != IntPtr.Zero;
        string label  = isSep ? string.Empty
                              : StripAccelerator(Marshal.PtrToStringUni(labelBuf) ?? string.Empty);

        // Canonical verb for leaf items.
        // Fast path: derive from the well-known label map (no COM round-trip).
        // Slow path: ask the shell via GetCommandString only for unknown labels.
        string verb = string.Empty;
        if (!isSep && !hasSub) {
          if (!string.IsNullOrEmpty(label) && _labelToVerb.TryGetValue(label, out var knownVerb)) {
            verb = knownVerb;
          } else if (_cm is not null) {
            try {
              uint offset = mii.wID >= CmdFirst ? mii.wID - CmdFirst : 0;
              Marshal.WriteInt16(verbBuf, 0);
              int vhr = _cm.GetCommandString(
                  (UIntPtr)offset, NativeShell.GCS_VERBW, IntPtr.Zero, verbBuf, VerbChars);
              if (vhr == 0)
                verb = Marshal.PtrToStringUni(verbBuf) ?? string.Empty;
            } catch { }
          }
        }

        // Extract HBITMAP icon pixels while still on the STA thread.
        byte[]? iconPx = null;
        int iconW = 0, iconH = 0;
        if (!isSep
            && mii.hbmpItem != IntPtr.Zero
            && mii.hbmpItem != NativeShell.MENUITEMINFOW.HBMMENU_CALLBACK) {
          try {
            var (px, pw, ph) = NativeShell.HBitmapToPixels(mii.hbmpItem);
            if (px is not null) { iconPx = px; iconW = pw; iconH = ph; }
          } catch { }
        }

        IReadOnlyList<ShellContextMenuItem>? subItems = null;
        if (hasSub) {
          // Fire WM_INITMENUPOPUP so lazy shell extensions (e.g. ESET, 7-Zip sub-
          // menus) populate their HMENU before we enumerate it.
          // lParam low-word = menu item index, high-word = 0 (not a system menu).
          IntPtr lParam = (IntPtr)((i & 0xFFFF) | 0); // MAKELPARAM(i, 0)
          // IContextMenu3 exposes HandleMenuMsg2 which returns a result pointer;
          // use it when available so the extension can do more work (e.g. library population).
          IntPtr _ignored = IntPtr.Zero;
          bool handled = false;
          try { if (_cm3 is not null) { _cm3.HandleMenuMsg2(WM_INITMENUPOPUP, mii.hSubMenu, lParam, out _ignored); handled = true; } } catch { }
          if (!handled) try { _cm2?.HandleMenuMsg(WM_INITMENUPOPUP, mii.hSubMenu, lParam); } catch { }

          // Only pump the STA message loop when the submenu is still empty after
          // the init message — those are the truly async extensions (e.g. Libraries,
          // ESET). Submenus that were populated synchronously skip the pump entirely,
          // eliminating the per-submenu delay for the common case. When a pump is
          // needed, return the instant the extension populates the submenu instead
          // of always burning the full worst-case timeout — most extensions finish
          // within 1-2ms, so this turns a flat 60ms tax per submenu into a few ms.
          IntPtr subMenu = mii.hSubMenu;
          if (NativeShell.GetMenuItemCount(subMenu) == 0)
            NativeShell.PumpMessagesUntil(() => NativeShell.GetMenuItemCount(subMenu) > 0, 60);

          subItems = BuildFromMenu(mii.hSubMenu);
        }

        list.Add(new ShellContextMenuItem(
            mii.wID, label, verb, isSep, disabled, hasSub, subItems,
            iconPx, iconW, iconH));
    }
    } finally {
      Marshal.FreeHGlobal(labelBuf);
      Marshal.FreeHGlobal(verbBuf);
    }

    // Remove consecutive separators and any trailing separator.
    for (int i = list.Count - 1; i >= 1; i--)
      if (list[i].IsSeparator && list[i - 1].IsSeparator)
        list.RemoveAt(i);
    while (list.Count > 0 && list[list.Count - 1].IsSeparator)
      list.RemoveAt(list.Count - 1);
    // Also drop a leading separator.
    while (list.Count > 0 && list[0].IsSeparator)
      list.RemoveAt(0);

    return list;
  }

  // Strips Win32 menu accelerator markers: &&  ->  &   and  &X  ->  X
  private static string StripAccelerator(string s) {
    if (s.IndexOf('&') < 0) return s;
    var sb = new System.Text.StringBuilder(s.Length);
    for (int i = 0; i < s.Length; i++) {
      if (s[i] == '&' && i + 1 < s.Length) {
        if (s[i + 1] == '&') { sb.Append('&'); i++; }
        // else: drop the & and let the next char be appended normally
      } else {
        sb.Append(s[i]);
      }
    }
    return sb.ToString();
  }

  // ── Command invocation ────────────────────────────────────────────────────

  /// <summary>
  /// Invokes a shell command by its absolute menu item ID on a dedicated STA thread,
  /// so that commands such as "New Folder" that require an STA COM pump work correctly.
  /// <paramref name="workingDir"/> is passed as <c>lpDirectory</c>/<c>lpDirectoryW</c>
  /// so commands that create files (New Folder, New Shortcut …) know where to act.
  /// </summary>
  public Task InvokeCommandAsync(uint cmdId, IntPtr hwnd, string? workingDir = null) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_cm is null) return Task.CompletedTask;

    uint offset = cmdId >= CmdFirst ? cmdId - CmdFirst : cmdId;
    return _sta.Run(() => {
      var cmi = new NativeShell.CMINVOKECOMMANDINFOEX {
        cbSize       = Marshal.SizeOf<NativeShell.CMINVOKECOMMANDINFOEX>(),
        fMask        = 0x00004000, // CMIC_MASK_UNICODE
        hwnd         = hwnd,
        lpVerb       = (IntPtr)(int)offset,
        lpVerbW      = (IntPtr)(int)offset,
        nShow        = 1, // SW_SHOWNORMAL
        lpDirectory  = IntPtr.Zero,
        lpDirectoryW = workingDir,
      };
      _cm!.InvokeCommand(ref cmi);
    });
  }

  /// <summary>Invokes a canonical shell verb on the owning STA thread.</summary>
  public Task InvokeVerbAsync(string verb, IntPtr hwnd, string? workingDir = null) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_cm is null) return Task.CompletedTask;

    return _sta.Run(() => {
      IntPtr verbA = Marshal.StringToHGlobalAnsi(verb);
      IntPtr verbW = Marshal.StringToHGlobalUni(verb);
      try {
        var cmi = new NativeShell.CMINVOKECOMMANDINFOEX {
          cbSize       = Marshal.SizeOf<NativeShell.CMINVOKECOMMANDINFOEX>(),
          fMask        = 0x00004000, // CMIC_MASK_UNICODE
          hwnd         = hwnd,
          lpVerb       = verbA,
          lpVerbW      = verbW,
          nShow        = 1,
          lpDirectory  = IntPtr.Zero,
          lpDirectoryW = workingDir,
        };
        _cm!.InvokeCommand(ref cmi);
      } finally {
        Marshal.FreeHGlobal(verbA);
        Marshal.FreeHGlobal(verbW);
      }
    });
  }

  // Keep the old sync wrappers for callers that still use them.
  /// <summary>Invokes a shell command by its absolute menu item ID.</summary>
  public void InvokeCommand(uint cmdId, IntPtr hwnd) =>
      InvokeCommandAsync(cmdId, hwnd).Wait();

  /// <summary>Invokes a canonical shell verb (e.g. "open", "delete", "properties").</summary>
  public void InvokeVerb(string verb, IntPtr hwnd) =>
      InvokeVerbAsync(verb, hwnd).Wait();

  // ── HandleMenuMsg forwarding (IContextMenu2/3) ────────────────────────────

  /// <summary>
  /// Forwards a Win32 menu message to <c>IContextMenu2::HandleMenuMsg</c>
  /// (or the <c>IContextMenu3</c> variant when available).
  /// Call this from a window subclass if you intercept WM_INITMENUPOPUP /
  /// WM_DRAWITEM / WM_MEASUREITEM.
  /// </summary>
  public int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam) {
    if (_cm3 is not null)
      return _cm3.HandleMenuMsg(uMsg, wParam, lParam);
    if (_cm2 is not null)
      return _cm2.HandleMenuMsg(uMsg, wParam, lParam);
    return 0;
  }

  /// <summary>
  /// Forwards to <c>IContextMenu3::HandleMenuMsg2</c> when available.
  /// </summary>
  public int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult) {
    if (_cm3 is not null)
      return _cm3.HandleMenuMsg2(uMsg, wParam, lParam, out plResult);
    plResult = IntPtr.Zero;
    return 0;
  }

  // ── IDisposable ───────────────────────────────────────────────────────────

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;

    if (_hMenuRoot != IntPtr.Zero) {
      NativeShell.DestroyMenu(_hMenuRoot);
      _hMenuRoot = IntPtr.Zero;
    }

    if (_cm3 is not null) {
      try { Marshal.ReleaseComObject(_cm3); } catch { }
      _cm3 = null;
    }
    if (_cm2 is not null) {
      try { Marshal.ReleaseComObject(_cm2); } catch { }
      _cm2 = null;
    }
    if (_cm is not null) {
      try { Marshal.ReleaseComObject(_cm); } catch { }
      _cm = null;
    }
    if (_ownsSta)
      _sta.Dispose();
  }
}

// ── Factory ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates <see cref="ShellContextMenuSession"/> instances on a dedicated STA thread.
/// Uses <c>SHBindToParent</c> + <c>IShellFolder.GetUIObjectOf</c> — the same path
/// the Windows shell uses internally for context menus.
/// </summary>
public static class ShellContextMenuService {
  private static readonly Guid IID_IShellFolder =
      new("000214E6-0000-0000-C000-000000000046");

  private static readonly Guid IID_IContextMenu =
      new("000214E4-0000-0000-C000-000000000046");

  // QueryContextMenu flags.
  private const uint CMF_NORMAL        = 0x00000000;
  private const uint CMF_EXTENDEDVERBS = 0x00000100;
  private const uint CMF_NODEFAULT     = 0x00000020;

  // ── Shared persistent STA thread ─────────────────────────────────────────
  // A single long-lived STA thread is reused across all context-menu queries.
  // This eliminates the thread-creation + COM STA initialisation overhead that
  // made the first two right-clicks noticeably slow.
  private static readonly StaWorker _sharedSta = new StaWorker();

  // Warm-up is a process-wide effect (loaded extension DLLs, JITted code paths) —
  // running it again for every new tab/ShellListView instance would just queue
  // redundant work onto the shared STA thread for no benefit. 0 = not started.
  private static int _warmUpStarted;
  private static Task? _warmUpTask;

  /// <summary>
  /// Pre-loads shell extension DLLs <em>and</em> exercises the full menu-build
  /// path (icon extraction, <c>GetCommandString</c> verb lookups, and the
  /// <c>WM_INITMENUPOPUP</c> + message-pump used for lazily-populated
  /// submenus such as "New" or "Send to") on the shared STA thread, so the
  /// first real right-click pays none of that cost. Call once early in the
  /// app lifecycle (e.g. from <c>ShellListView.Loaded</c>) and discard the
  /// returned Task — it completes in the background without blocking the UI.
  ///
  /// Three throw-away queries are built and fully walked via
  /// <see cref="ShellContextMenuSession.BuildItems"/>:
  /// 1. Desktop folder background — loads <c>HKCR\Directory\Background</c>,
  ///    <c>HKCR\Folder</c>, and <c>HKCR\AllFilesystemObjects</c> handlers.
  /// 2. A guaranteed system file — loads <c>HKCR\*</c> handlers (applies to
  ///    every file type, so antivirus / archive helpers etc. are pre-loaded).
  /// 3. A guaranteed system folder — loads <c>HKCR\Directory</c> /
  ///    <c>HKCR\Folder</c> item (not background) handlers, e.g. "pin to Quick
  ///    access", cloud-sync overlays' submenus, etc.
  /// </summary>
  public static Task WarmUpAsync() {
    if (Interlocked.CompareExchange(ref _warmUpStarted, 1, 0) == 0) {
      _warmUpTask = _sharedSta.Run(() => {
        WarmUpDesktopBackground();
        WarmUpItem(System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        WarmUpItem(Environment.SystemDirectory);
      });
    }
    return _warmUpTask!;
  }

  // Loads Folder / Directory\Background / AllFilesystemObjects shell handlers,
  // and fully walks the resulting menu (submenus, verbs, icons) so those code
  // paths are JITted/cached ahead of the first real background right-click.
  private static void WarmUpDesktopBackground() {
    try {
      int hr = NativeShell.SHGetDesktopFolder(out object desktopObj);
      if (hr != 0 || desktopObj is not NativeShell.IShellFolderCM desktop) return;
      try {
        hr = desktop.CreateViewObject(IntPtr.Zero, IID_IContextMenu, out object cmObj);
        if (hr != 0 || cmObj is not NativeShell.IContextMenuCM cm) return;
        BuildAndDiscard(cm);
      } finally { try { Marshal.ReleaseComObject(desktop); } catch { } }
    } catch { }
  }

  // Loads HKCR\* (or HKCR\Directory/Folder for a directory path) and
  // per-extension handlers, then fully walks the resulting menu the same way
  // WarmUpDesktopBackground does. Works for both files and folders.
  private static void WarmUpItem(string path) {
    if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) return;
    IntPtr pidl = IntPtr.Zero;
    NativeShell.IShellFolderCM? parent = null;
    try {
      pidl = NativeShell.ILCreateFromPathW(path);
      if (pidl == IntPtr.Zero) return;

      int hr = NativeShell.SHBindToParent(
          pidl, IID_IShellFolder, out object folderObj, out IntPtr childPidl);
      if (hr != 0 || folderObj is not NativeShell.IShellFolderCM folder) return;
      parent = folder;

      var children = new[] { childPidl };
      hr = folder.GetUIObjectOf(IntPtr.Zero, 1, children,
           IID_IContextMenu, IntPtr.Zero, out object cmObj);
      if (hr != 0 || cmObj is not NativeShell.IContextMenuCM cm) return;
      BuildAndDiscard(cm);
    } catch { }
    finally {
      if (pidl != IntPtr.Zero) NativeShell.ILFree(pidl);
      if (parent is not null) try { Marshal.ReleaseComObject(parent); } catch { }
    }
  }

  // Populates the HMENU for a throw-away IContextMenu and fully walks it via
  // ShellContextMenuSession.BuildItems — same work a real right-click does —
  // then tears it down. Must be called on the shared STA thread.
  private static void BuildAndDiscard(NativeShell.IContextMenuCM cm) {
    try {
      IntPtr hMenu = NativeShell.CreatePopupMenu();
      if (hMenu == IntPtr.Zero) { try { Marshal.ReleaseComObject(cm); } catch { } return; }

      cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL);

      NativeShell.IContextMenu3CM? cm3 = cm as NativeShell.IContextMenu3CM;
      NativeShell.IContextMenu2CM? cm2 = cm3 is null ? cm as NativeShell.IContextMenu2CM : null;

      var session = new ShellContextMenuSession(cm, cm2, cm3, hMenu, _sharedSta, ownsSta: false);
      try { session.BuildItems(); } catch { }
      session.Dispose();
    } catch { try { Marshal.ReleaseComObject(cm); } catch { } }
  }

  public static Task<ShellContextMenuSession?> QueryAsync(
      IReadOnlyList<string> paths,
      IntPtr hwnd,
      bool extendedVerbs = false) {
    var tcs = new TaskCompletionSource<ShellContextMenuSession?>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    var sta = _sharedSta;

    sta.Run(() => {
      var allPidls    = new IntPtr[paths.Count];
      var childPidls  = new IntPtr[paths.Count];
      NativeShell.IShellFolderCM? parentFolder = null;

      try {
        for (int i = 0; i < paths.Count; i++) {
          allPidls[i] = NativeShell.ILCreateFromPathW(paths[i]);
          if (allPidls[i] == IntPtr.Zero) { tcs.SetResult(null); return; }
        }

        int hr = NativeShell.SHBindToParent(
            allPidls[0], IID_IShellFolder, out object folderObj, out childPidls[0]);
        if (hr != 0 || folderObj is not NativeShell.IShellFolderCM folder) {
          tcs.SetResult(null); return;
        }
        parentFolder = folder;

        for (int i = 1; i < paths.Count; i++) {
          hr = NativeShell.SHBindToParent(
              allPidls[i], IID_IShellFolder, out _, out childPidls[i]);
          if (hr != 0 || childPidls[i] == IntPtr.Zero) {
            tcs.SetResult(null); return;
          }
        }

        hr = parentFolder.GetUIObjectOf(
            hwnd, (uint)paths.Count, childPidls,
            IID_IContextMenu, IntPtr.Zero, out object cmObj);
        if (hr != 0 || cmObj is not NativeShell.IContextMenuCM cm) {
          tcs.SetResult(null); return;
        }

        NativeShell.IContextMenu3CM? cm3 = cmObj as NativeShell.IContextMenu3CM;
        NativeShell.IContextMenu2CM? cm2 = cm3 is null
            ? cmObj as NativeShell.IContextMenu2CM
            : null;

        IntPtr hMenu = NativeShell.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) {
          try { Marshal.ReleaseComObject(cm); } catch { }
          tcs.SetResult(null); return;
        }

        uint flags = CMF_NORMAL;
        if (extendedVerbs) flags |= CMF_EXTENDEDVERBS;

        cm.QueryContextMenu(hMenu, 0,
            ShellContextMenuSession.CmdFirst,
            ShellContextMenuSession.CmdLast,
            flags);

        var session = new ShellContextMenuSession(cm, cm2, cm3, hMenu, sta, ownsSta: false);
        session.BuildItems();
        tcs.SetResult(session);
      } catch (Exception ex) {
        tcs.SetException(ex);
      } finally {
        foreach (var p in allPidls)
          if (p != IntPtr.Zero) NativeShell.ILFree(p);
        if (parentFolder is not null)
          try { Marshal.ReleaseComObject(parentFolder); } catch { }
      }
    });

    return tcs.Task;
  }

  /// <summary>
  /// Queries the background
  /// <c>IShellFolder::CreateViewObject(IID_IContextMenu)</c> on the folder itself.
  /// This is the same COM path that the shell uses for <c>IShellView::GetItemObject(SVGIO_BACKGROUND)</c>.
  /// </summary>
  public static Task<ShellContextMenuSession?> QueryBackgroundAsync(
      string folderPath,
      IntPtr hwnd,
      bool   extendedVerbs = false) {
    var tcs = new TaskCompletionSource<ShellContextMenuSession?>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    var sta = _sharedSta;

    sta.Run(() => {
      IntPtr folderPidl = IntPtr.Zero;
      NativeShell.IShellFolderCM? folder = null;

      try {
        folderPidl = NativeShell.ILCreateFromPathW(folderPath);
        if (folderPidl == IntPtr.Zero) { tcs.SetResult(null); return; }

        int hr = NativeShell.SHGetDesktopFolder(out object desktopObj);
        if (hr != 0 || desktopObj is not NativeShell.IShellFolderCM desktop) {
          tcs.SetResult(null); return;
        }

        hr = desktop.BindToObject(folderPidl, IntPtr.Zero, IID_IShellFolder, out object folderObj);
        Marshal.ReleaseComObject(desktop);

        if (hr != 0 || folderObj is not NativeShell.IShellFolderCM targetFolder) {
          if (folderObj is NativeShell.IShellFolderCM fb) targetFolder = fb;
          else { tcs.SetResult(null); return; }
        }
        folder = targetFolder;

        hr = folder.CreateViewObject(hwnd, IID_IContextMenu, out object cmObj);
        if (hr != 0 || cmObj is not NativeShell.IContextMenuCM cm) {
          tcs.SetResult(null); return;
        }

        NativeShell.IContextMenu3CM? cm3 = cmObj as NativeShell.IContextMenu3CM;
        NativeShell.IContextMenu2CM? cm2 = cm3 is null
            ? cmObj as NativeShell.IContextMenu2CM
            : null;

        IntPtr hMenu = NativeShell.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) {
          try { Marshal.ReleaseComObject(cm); } catch { }
          tcs.SetResult(null); return;
        }

        uint flags = CMF_NORMAL;
        if (extendedVerbs) flags |= CMF_EXTENDEDVERBS;

        cm.QueryContextMenu(hMenu, 0,
            ShellContextMenuSession.CmdFirst,
            ShellContextMenuSession.CmdLast,
            flags);

        var session = new ShellContextMenuSession(cm, cm2, cm3, hMenu, sta, ownsSta: false);
        session.BuildItems();
        tcs.SetResult(session);
      } catch (Exception ex) {
        tcs.SetException(ex);
      } finally {
        if (folderPidl != IntPtr.Zero) NativeShell.ILFree(folderPidl);
        if (folder is not null) try { Marshal.ReleaseComObject(folder); } catch { }
      }
    });

    return tcs.Task;
  }
}
