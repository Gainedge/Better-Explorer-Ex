    // -- Hide-until-first-render --------------------------------------------------

    // Held to prevent the GC collecting the delegate while the subclass is active.
    private SubclassProc? _subclassProc;

    private void RegisterShowAfterFirstRender()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // WM_SHOWWINDOW fires synchronously INSIDE ShowWindow(), before DWM has
        // composited a single frame -- the earliest possible interception point.
        SubclassProc? proc = null;
        proc = (h, msg, wp, lp, id, _) =>
        {
            if (msg == WM_SHOWWINDOW && wp == 1)
            {
                nint result = DefSubclassProc(h, msg, wp, lp);
                RemoveWindowSubclass(h, proc!, id);
                _subclassProc = null;
                ShowWindow(h, SW_HIDE);
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => ShowWindow(h, SW_SHOW));
                return result;
            }
            return DefSubclassProc(h, msg, wp, lp);
        };
        _subclassProc = proc;
        SetWindowSubclass(hwnd, _subclassProc, 1, 0);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);
    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc pfnSubclass, nuint uIdSubclass);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint uMsg, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    private const int  SW_HIDE       = 0;
    private const int  SW_SHOW       = 5;
    private const uint WM_SHOWWINDOW = 0x0018;
