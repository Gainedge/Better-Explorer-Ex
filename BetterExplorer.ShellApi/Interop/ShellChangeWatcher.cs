using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterExplorer.ShellApi.Interop;

/// <summary>
/// Receives shell change notifications for a folder path — both real filesystem paths
/// (e.g. <c>C:\Users\Foo</c>) and virtual shell-namespace paths (e.g. <c>::{GUID}</c>) —
/// by registering with <c>SHChangeNotifyRegister</c> on a dedicated hidden HWND.
/// <para>
/// Uses <c>SHCNRF_SHELLLEVEL</c> for all paths so that notifications are always delivered
/// via the shell notification service and decoded with <c>SHChangeNotification_Lock</c>.
/// (<c>SHCNRF_INTERRUPTLEVEL</c> is intentionally avoided because it delivers the WM via
/// SendMessage with raw PIDLs instead of an hChange handle, which breaks the lock/unlock flow.)
/// </para>
/// <para>
/// Usage: create, subscribe to <see cref="Changed"/>, call <see cref="Start"/>, call
/// <see cref="Dispose"/> when done.
/// </para>
/// </summary>
public sealed class ShellChangeWatcher : IDisposable {
  // ── Win32 constants ──────────────────────────────────────────────────────

  // Shell-change event flags — we register for everything that matters to a folder view.
  private const uint SHCNE_DRIVEADD        = 0x00000100;
  private const uint SHCNE_DRIVEREMOVED    = 0x00000080;
  private const uint SHCNE_MEDIAINSERTED   = 0x00000020;
  private const uint SHCNE_MEDIAREMOVED    = 0x00000040;
  private const uint SHCNE_NETSHARE        = 0x00000200;
  private const uint SHCNE_NETUNSHARE      = 0x00000400;
  private const uint SHCNE_CREATE          = 0x00000002;
  private const uint SHCNE_DELETE          = 0x00000004;
  private const uint SHCNE_MKDIR           = 0x00000008;
  private const uint SHCNE_RMDIR           = 0x00000010;
  private const uint SHCNE_RENAME_ITEM     = 0x00000001;
  private const uint SHCNE_RENAME_FOLDER   = 0x20000000;
  private const uint SHCNE_UPDATEDIR       = 0x00001000;
  private const uint SHCNE_UPDATEITEM      = 0x00002000;
  private const uint SHCNE_FREESPACE       = 0x00040000;
  private const uint SHCNE_ASSOCCHANGED    = 0x08000000;
  private const uint SHCNE_DISKEVENTS      = 0x0002381F;

  // SHChangeNotifyRegister flags
  // SHCNRF_INTERRUPTLEVEL — watches the FS driver layer; REQUIRED for real paths.
  // Without it only shell-broker broadcasts are received, missing most raw FS events.
  private const uint SHCNRF_INTERRUPTLEVEL     = 0x0001;
  private const uint SHCNRF_SHELLLEVEL         = 0x0002;
  private const uint SHCNRF_RECURSIVEINTERRUPT = 0x1000;
  // SHCNRF_NEWDELIVERY converts ALL delivery (interrupt + shell level) to the
  // lock-based format: wParam = hChange, lParam = dwProcessId.
  // It is safe to combine with SHCNRF_INTERRUPTLEVEL — the earlier concern about
  // raw-PIDL delivery only applies when NEWDELIVERY is absent.
  private const uint SHCNRF_NEWDELIVERY        = 0x8000;

  private const int WM_USER = 0x0400;
  // Primary shell-change notification message we register for.
  private const int WM_SHELLCHANGE          = WM_USER + 1;
  // Shell also posts wMsg+1 as a "delivery complete" signal; we absorb it.
  private const int WM_SHELLCHANGE_DELIVERY = WM_USER + 2;
  private const int WM_DESTROY = 0x0002;
  private const int WM_CLOSE   = 0x0010;

  private static readonly IntPtr HWND_MESSAGE = new(-3);
  private const int CS_NOCLOSE = 0x0200;

  // ── P/Invoke ─────────────────────────────────────────────────────────────

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct WNDCLASSEX {
    public uint     cbSize;
    public uint     style;
    public IntPtr   lpfnWndProc;
    public int      cbClsExtra;
    public int      cbWndExtra;
    public IntPtr   hInstance;
    public IntPtr   hIcon;
    public IntPtr   hCursor;
    public IntPtr   hbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string?  lpszMenuName;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string   lpszClassName;
    public IntPtr   hIconSm;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct MSG {
    public IntPtr hwnd;
    public uint   message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint   time;
    public int    ptX, ptY;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct SHChangeNotifyEntry {
    public IntPtr pidl;
    [MarshalAs(UnmanagedType.Bool)]
    public bool   fRecursive;
  }

  private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr CreateWindowEx(
      uint dwExStyle, string lpClassName, string lpWindowName,
      uint dwStyle, int x, int y, int nWidth, int nHeight,
      IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

  [DllImport("user32.dll")]
  private static extern bool DestroyWindow(IntPtr hWnd);

  [DllImport("user32.dll")]
  private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

  [DllImport("user32.dll")]
  private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

  [DllImport("user32.dll")]
  private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

  [DllImport("user32.dll")]
  private static extern bool TranslateMessage(ref MSG lpMsg);

  [DllImport("user32.dll")]
  private static extern IntPtr DispatchMessage(ref MSG lpmsg);

  [DllImport("kernel32.dll")]
  private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

  // SHChangeNotifyRegister — shell32.dll ordinal 2 (undocumented) is also exported by name on Win10+.
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  private static extern uint SHChangeNotifyRegister(
      IntPtr hWnd, uint fSources, uint fEvents, uint wMsg,
      int cEntries, [In] ref SHChangeNotifyEntry pshcne);

  [DllImport("shell32.dll")]
  private static extern bool SHChangeNotifyDeregister(uint ulID);

  // SHChangeNotification_Lock — lets us read the two PIDLs from the notification.
  [DllImport("shell32.dll")]
  private static extern IntPtr SHChangeNotification_Lock(
      IntPtr hChange, uint dwProcId, out IntPtr ppidl, out uint plEvent);

  [DllImport("shell32.dll")]
  private static extern bool SHChangeNotification_Unlock(IntPtr hLock);

  // PIDL → display name helper
  [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
  private static extern void SHGetNameFromIDList(
      IntPtr pidl, uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

  // SHGetKnownFolderIDList — turn a FOLDERID into a PIDL for registration
  [DllImport("shell32.dll")]
  private static extern int SHGetKnownFolderIDList(
      [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags,
      IntPtr hToken, out IntPtr ppidl);

  // Parse a shell path (including ::{GUID}) into a PIDL
  [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
  private static extern void SHParseDisplayName(
      [MarshalAs(UnmanagedType.LPWStr)] string pszName,
      IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

  [DllImport("ole32.dll")]
  private static extern void CoTaskMemFree(IntPtr pv);

  private const int GWLP_USERDATA = -21;
  private const uint SIGDN_FILESYSPATH              = 0x80058000;
  private const uint SIGDN_DESKTOPABSOLUTEPARSING   = 0x80028000; // fallback for non-FS items

  // ── Window-class registration (once per process) ─────────────────────────

  private static readonly string WndClassName = "BEShellChangeWatcher_" + Environment.ProcessId;
  private static int _classRegistered;          // 0 = unregistered, 1 = registered
  private static readonly object _classLock = new();

  private static void EnsureWindowClassRegistered(WndProcDelegate proc) {
    if (Volatile.Read(ref _classRegistered) == 1)
      return;
    lock (_classLock) {
      if (_classRegistered == 1)
        return;
      var wc = new WNDCLASSEX {
        cbSize      = (uint)Marshal.SizeOf<WNDCLASSEX>(),
        style       = 0,
        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
        hInstance   = GetModuleHandle(IntPtr.Zero),
        lpszClassName = WndClassName,
      };
      if (RegisterClassEx(ref wc) == 0)
        throw new InvalidOperationException(
            $"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
      Volatile.Write(ref _classRegistered, 1);
    }
  }

  // ── Instance state ────────────────────────────────────────────────────────

  private readonly string _path;           // real FS path or ::{GUID}
  private readonly bool   _isVirtual;      // true when _path is a shell-namespace path
  private Thread?   _thread;
  private IntPtr    _hwnd;
  private uint      _notifyId;
  private GCHandle  _selfHandle;
  private bool      _disposed;

  // Raised on the background thread — caller must marshal to UI.
  public event EventHandler<ShellChangeEventArgs>? Changed;

  public ShellChangeWatcher(string path) {
    _path      = path;
    _isVirtual = path.StartsWith("::", StringComparison.Ordinal);
  }

  /// <summary>Starts the message pump and registers for shell notifications.
  /// Returns immediately; the pump runs on its own STA thread.</summary>
  public void Start() {
    if (_disposed || _thread is not null)
      return;

    _thread = new Thread(RunMessagePump) {
      IsBackground = true,
      Name         = "ShellChangeWatcher"
    };
    _thread.SetApartmentState(ApartmentState.STA);
    _thread.Start();
  }

  // ── Message pump (runs on _thread) ───────────────────────────────────────

  // We keep a single static delegate alive for the lifetime of the process so
  // the GC never collects the function pointer used by the registered class.
  // The per-instance pointer is stored in GWLP_USERDATA.
  private static readonly WndProcDelegate _staticWndProc = StaticWndProc;

  private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
    // Retrieve the instance pointer stored in GWLP_USERDATA.
    var userData = GetWindowLongPtr(hWnd, GWLP_USERDATA);
    if (userData != IntPtr.Zero) {
      var handle = GCHandle.FromIntPtr(userData);
      if (handle.IsAllocated && handle.Target is ShellChangeWatcher watcher)
        return watcher.WndProc(hWnd, msg, wParam, lParam);
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
  }

  private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
    if (msg == WM_SHELLCHANGE) {
      // wParam = hChange, lParam = dwProcId  (SHCNRF_NEWDELIVERY format)
      ProcessShellChange(wParam, (uint)(long)lParam);
      return IntPtr.Zero;
    }
    if (msg == WM_SHELLCHANGE_DELIVERY) {
      // Shell posts wMsg+1 as a delivery-complete signal; absorb it.
      return IntPtr.Zero;
    }
    if (msg == WM_DESTROY) {
      // Message pump will exit after PostQuitMessage.
      UnregisterNotify();
      if (_selfHandle.IsAllocated) {
        _selfHandle.Free();
      }
      // Post WM_QUIT (value 0x0012) to break GetMessage loop.
      PostMessage(hWnd, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
      return IntPtr.Zero;
    }
    return DefWindowProc(hWnd, msg, wParam, lParam);
  }

  private void ProcessShellChange(IntPtr hChange, uint procId) {
    IntPtr ppidl;
    uint   plEvent;
    var hLock = SHChangeNotification_Lock(hChange, procId, out ppidl, out plEvent);
    if (hLock == IntPtr.Zero)
      return;
    try {
      // Decode path1 and (for rename events) path2 — ppidl is a 2-element PIDL array.
      string? path1 = null, path2 = null;
      if (ppidl != IntPtr.Zero) {
        try {
          var pidl1 = Marshal.ReadIntPtr(ppidl, 0);
          if (pidl1 != IntPtr.Zero)
            path1 = PidlToPath(pidl1);
          var pidl2 = Marshal.ReadIntPtr(ppidl, IntPtr.Size);
          if (pidl2 != IntPtr.Zero)
            path2 = PidlToPath(pidl2);
        } catch { }
      }
      Changed?.Invoke(this, new ShellChangeEventArgs((ShellChangeType)plEvent, path1, path2));
    } finally {
      SHChangeNotification_Unlock(hLock);
    }
  }

  // Try SIGDN_FILESYSPATH first; fall back to SIGDN_DESKTOPABSOLUTEPARSING
  // for virtual shell items that have no FS path.
  private static string? PidlToPath(IntPtr pidl) {
    try {
      SHGetNameFromIDList(pidl, SIGDN_FILESYSPATH, out var name);
      if (!string.IsNullOrEmpty(name)) return name;
    } catch { }
    try {
      SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, out var name);
      return string.IsNullOrEmpty(name) ? null : name;
    } catch { }
    return null;
  }

  private void RunMessagePump() {
    // Ensure the window class is registered — pass the static proc only the first time.
    EnsureWindowClassRegistered(_staticWndProc);

    // Pin this instance so the GC doesn't move it while its pointer is stored in GWLP_USERDATA.
    _selfHandle = GCHandle.Alloc(this);

    _hwnd = CreateWindowEx(0, WndClassName, string.Empty, 0,
        0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero,
        GetModuleHandle(IntPtr.Zero), IntPtr.Zero);

    if (_hwnd == IntPtr.Zero) {
      if (_selfHandle.IsAllocated)
        _selfHandle.Free();
      return;
    }

    // Store 'this' pointer so the static WndProc can dispatch to the instance.
    SetWindowLongPtr(_hwnd, GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));

    // Register for shell change notifications on the watched path.
    RegisterNotify();

    // Standard Win32 message loop.
    while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) {
      TranslateMessage(ref msg);
      DispatchMessage(ref msg);
    }

    // If the window was not destroyed via WM_DESTROY (e.g. Dispose was called
    // from another thread before the pump started fully), clean up here.
    if (_notifyId != 0) {
      SHChangeNotifyDeregister(_notifyId);
      _notifyId = 0;
    }
  }

  private void RegisterNotify() {
    IntPtr pidl = IntPtr.Zero;
    bool freePidl = true;
    try {
      // Strip trailing backslash — SHParseDisplayName rejects "C:\Foo\" but accepts "C:\Foo".
      var parsePath = _path.TrimEnd('\\', '/');
      try {
        SHParseDisplayName(parsePath, IntPtr.Zero, out pidl, 0, out _);
      } catch {
        pidl = IntPtr.Zero;
      }

      if (pidl == IntPtr.Zero && _isVirtual) {
        // Fallback for ::{GUID} paths — try SHGetKnownFolderIDList.
        if (_path.StartsWith("::{", StringComparison.OrdinalIgnoreCase) &&
            _path.Length >= 39) {
          var guidStr = _path[2..];   // strip leading ::
          if (Guid.TryParse(guidStr, out var guid))
            SHGetKnownFolderIDList(guid, 0, IntPtr.Zero, out pidl);
        }
      }

      if (pidl == IntPtr.Zero)
        return;

      var entry = new SHChangeNotifyEntry { pidl = pidl, fRecursive = false };

      // Events we care about: drives, media, network shares, folder contents, renames.
      const uint events =
          SHCNE_DRIVEADD      | SHCNE_DRIVEREMOVED   |
          SHCNE_MEDIAINSERTED | SHCNE_MEDIAREMOVED    |
          SHCNE_NETSHARE      | SHCNE_NETUNSHARE      |
          SHCNE_CREATE        | SHCNE_DELETE          |
          SHCNE_MKDIR         | SHCNE_RMDIR           |
          SHCNE_RENAME_ITEM   | SHCNE_RENAME_FOLDER   |
          SHCNE_UPDATEDIR     | SHCNE_UPDATEITEM      |
          SHCNE_FREESPACE     | SHCNE_ASSOCCHANGED;

      // Real FS paths need SHCNRF_INTERRUPTLEVEL to receive filesystem-driver events
      // (create, delete, write).  Without it only shell-broker broadcasts arrive,
      // which are sporadic and miss most plain file operations.
      // SHCNRF_NEWDELIVERY converts ALL sources (interrupt + shell) to the
      // hChange/procId format required by SHChangeNotification_Lock — it is safe
      // to combine with SHCNRF_INTERRUPTLEVEL.
      uint sources = _isVirtual
          ? SHCNRF_SHELLLEVEL | SHCNRF_RECURSIVEINTERRUPT | SHCNRF_NEWDELIVERY
          : SHCNRF_INTERRUPTLEVEL | SHCNRF_SHELLLEVEL | SHCNRF_NEWDELIVERY;

      _notifyId = SHChangeNotifyRegister(
          _hwnd,
          sources,
          events,
          WM_SHELLCHANGE,
          1,
          ref entry);
    } finally {
      if (freePidl && pidl != IntPtr.Zero)
        CoTaskMemFree(pidl);
    }
  }

  private void UnregisterNotify() {
    if (_notifyId != 0) {
      SHChangeNotifyDeregister(_notifyId);
      _notifyId = 0;
    }
  }

  // ── IDisposable ───────────────────────────────────────────────────────────

  public void Dispose() {
    if (_disposed)
      return;
    _disposed = true;

    // Ask the message-pump thread to shut down by posting WM_CLOSE to our HWND.
    // The DefWindowProc handler for WM_CLOSE posts WM_DESTROY, which breaks the loop.
    if (_hwnd != IntPtr.Zero) {
      PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
      _hwnd = IntPtr.Zero;
    }
  }
}

// ── Supporting types ─────────────────────────────────────────────────────────

/// <summary>Categories of shell change event, matching the SHCNE_* flags.</summary>
[Flags]
public enum ShellChangeType : uint {
  RenameItem     = 0x00000001,
  Create         = 0x00000002,
  Delete         = 0x00000004,
  MkDir          = 0x00000008,
  RmDir          = 0x00000010,
  MediaInserted  = 0x00000020,
  MediaRemoved   = 0x00000040,
  DriveRemoved   = 0x00000080,
  DriveAdd       = 0x00000100,
  NetShare       = 0x00000200,
  NetUnshare     = 0x00000400,
  UpdateItem     = 0x00002000,
  UpdateDir      = 0x00001000,
  FreeSpace      = 0x00040000,
  RenameFolder   = 0x20000000,
  AssocChanged   = 0x08000000,
}

public sealed class ShellChangeEventArgs : EventArgs {
  public ShellChangeType EventType { get; }
  /// <summary>FS path of the first item involved, or null for virtual/pidl-only items.</summary>
  public string? Path { get; }
  /// <summary>FS path of the second item involved (new path for rename events), or null.</summary>
  public string? Path2 { get; }

  public ShellChangeEventArgs(ShellChangeType eventType, string? path, string? path2 = null) {
    EventType = eventType;
    Path  = path;
    Path2 = path2;
  }
}
