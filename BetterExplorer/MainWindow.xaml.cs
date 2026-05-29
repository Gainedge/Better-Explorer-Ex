using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;

namespace BetterExplorer;

public sealed partial class MainWindow : Window
{
    // Height of the TabView tab strip in device-independent pixels.
    // WinUI 3 TabView default; used when no live measurement is available.
    private const double TabStripDips = 40;

    private InputNonClientPointerSource? _nonClientSource;

    // ── Settings keys ─────────────────────────────────────────────────────────
    private const string SettingX         = "Window.X";
    private const string SettingY         = "Window.Y";
    private const string SettingWidth     = "Window.Width";
    private const string SettingHeight    = "Window.Height";
    private const string SettingPresenter = "Window.Presenter"; // "Normal" | "Maximized" | "Minimized"

    // Last known restored (non-maximized, non-minimized) rect in screen pixels.
    private RectInt32 _restoredRect;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;

        // Caption buttons blend into the Mica backdrop.
        AppWindow.TitleBar.ButtonBackgroundColor         = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        AppWindow.SetIcon("BENewIcon.ico");

        // Apply persisted theme AFTER ExtendsContentIntoTitleBar so that
        // DWM attributes (DWMWA_USE_IMMERSIVE_DARK_MODE) are not reset by the
        // title-bar extension call.
        ApplyPersistedTheme();

        _nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        RestoreWindowPlacement();

        TabbedBrowser.Loaded   += OnTabbedBrowserLoaded;
        SizeChanged            += OnWindowSizeChanged;
        Closed                 += OnWindowClosed;

        BetterExplorer.Controls.SettingsPage.ThemeChangeRequested += OnThemeChangeRequested;

        // Hide the HWND the instant it is first activated so DWM never
        // composites the initial black frame to the screen.  The window is
        // revealed once the dispatcher has flushed the first render pass.
        RegisterShowAfterFirstRender();
    }

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

    // ── Window placement persistence

    private void RestoreWindowPlacement()
    {
        var settings = ApplicationData.Current.LocalSettings.Values;

        // Restore size / position only when both were previously saved.
        if (settings.TryGetValue(SettingX,      out var ox) &&
            settings.TryGetValue(SettingY,      out var oy) &&
            settings.TryGetValue(SettingWidth,  out var ow) &&
            settings.TryGetValue(SettingHeight, out var oh))
        {
            var rect = new RectInt32(
                (int)ox, (int)oy, (int)ow, (int)oh);
            AppWindow.MoveAndResize(rect);
            _restoredRect = rect;
        }

        // Restore presenter state (Maximized / Minimized / Normal).
        var presenterStr = settings.TryGetValue(SettingPresenter, out var op) ? op as string : null;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            switch (presenterStr)
            {
                case "Maximized": presenter.Maximize();  break;
                case "Minimized": presenter.Minimize();  break;
                // "Normal" or null: leave as-is (already restored size above).
            }
        }
    }

    private void SaveWindowPlacement()
    {
        var settings = ApplicationData.Current.LocalSettings.Values;

        string presenterState = "Normal";
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            presenterState = p.State switch
            {
                OverlappedPresenterState.Maximized => "Maximized",
                OverlappedPresenterState.Minimized => "Minimized",
                _                                  => "Normal",
            };
        }

        // Always save the restored rect (tracked via _restoredRect in SizeChanged)
        // so reopening from maximized still restores to the right size/position.
        if (_restoredRect.Width > 0 && _restoredRect.Height > 0)
        {
            settings[SettingX]      = _restoredRect.X;
            settings[SettingY]      = _restoredRect.Y;
            settings[SettingWidth]  = _restoredRect.Width;
            settings[SettingHeight] = _restoredRect.Height;
        }

        settings[SettingPresenter] = presenterState;
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        BetterExplorer.Controls.SettingsPage.ThemeChangeRequested -= OnThemeChangeRequested;
        SaveWindowPlacement();
    }

    private void OnThemeChangeRequested(Microsoft.UI.Xaml.ElementTheme theme)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = theme;
        UpdateTitleBarButtonColors(theme);
    }

    private void UpdateTitleBarButtonColors(ElementTheme theme)
    {
        bool isDark = theme == ElementTheme.Dark ||
                     (theme == ElementTheme.Default &&
                      Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var fg = isDark ? Colors.White : Colors.Black;
        var inactiveFg = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80)
            : Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A);
        AppWindow.TitleBar.ButtonForegroundColor         = fg;
        AppWindow.TitleBar.ButtonHoverForegroundColor    = fg;
        AppWindow.TitleBar.ButtonPressedForegroundColor  = fg;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = inactiveFg;

        // Tell DWM the dark/light preference and the backdrop type so it can
        // render Mica from the very first frame, before the WinUI MicaController
        // is attached (which only happens on the Activated event).
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int dark = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int backdropType = DWMSBT_TABBEDWINDOW; // Mica Alt (BaseAlt)
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE     = 38;
    private const int DWMSBT_TABBEDWINDOW           = 4;  // Mica Alt

    private void ApplyPersistedTheme()
    {
        var saved = Windows.Storage.ApplicationData.Current.LocalSettings.Values
            .TryGetValue(BetterExplorer.Controls.SettingsPage.ThemeSettingKey, out var v) ? v as string : null;
        var theme = saved switch {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default,
        };
        if (Content is FrameworkElement root)
            root.RequestedTheme = theme;
        UpdateTitleBarButtonColors(theme);
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private void OnTabbedBrowserLoaded(object sender, RoutedEventArgs e)
    {
        // Keep insets (TitleBarDragRight width) current for the tab footer.
        ApplyTitleBarInsets();

        // Recompute non-client regions after every layout pass.
        // This covers tab add/remove, tab resize, and window resize.
        TabbedBrowser.TabControl.LayoutUpdated +=
            (_, _) => DispatcherQueue.TryEnqueue(UpdateNonClientRegions);

        UpdateNonClientRegions();
    }

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e)
    {
        ApplyTitleBarInsets();
        UpdateNonClientRegions();

        // Track the restored rect so SaveWindowPlacement can use it even when
        // the window is closed while maximized.
        if (AppWindow.Presenter is OverlappedPresenter op &&
            op.State == OverlappedPresenterState.Restored)
        {
            _restoredRect = new RectInt32(
                AppWindow.Position.X, AppWindow.Position.Y,
                AppWindow.Size.Width, AppWindow.Size.Height);
        }
    }

    // ── Non-client region management ──────────────────────────────────────────

    /// <summary>
    /// Registers the tab strip with <see cref="InputNonClientPointerSource"/>:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Caption</b> — the full-width tab strip row.  Empty space in the strip
    ///     returns HTCAPTION so the user can drag the window from there.
    ///   </item>
    ///   <item>
    ///     <b>Passthrough</b> — the bounds of every <see cref="TabViewItem"/> and the
    ///     Add button.  These positions always return HTCLIENT, preserving tab clicks,
    ///     close × clicks, and — crucially — tab drag-reorder, which requires that
    ///     HTCLIENT is returned consistently throughout the entire drag gesture.
    ///   </item>
    /// </list>
    /// </summary>
    private void UpdateNonClientRegions()
    {
        if (_nonClientSource is null || TabbedBrowser.XamlRoot is null || AppWindow is null) return;

        var scale       = (float)TabbedBrowser.XamlRoot.RasterizationScale;
        var stripHeight = GetTabStripHeightPx(scale);
        var windowWidth = AppWindow.Size.Width;

        // ── Caption: the entire tab strip row ────────────────────────────────
        _nonClientSource.SetRegionRects(
            NonClientRegionKind.Caption,
            [new RectInt32(0, 0, windowWidth, stripHeight)]);

        // ── Passthrough: every TabViewItem + the Add (+) button ──────────────
        var passthrough = new List<RectInt32>();
        var tabControl  = TabbedBrowser.TabControl;

        foreach (var obj in tabControl.TabItems)
        {
            if (tabControl.ContainerFromItem(obj) is TabViewItem item)
            {
                var r = ElementToWindowRect(item, scale);
                if (r.HasValue) passthrough.Add(r.Value);
            }
        }

        // The Add button lives inside the TabView template with name "AddButton".
        var addButton = FindDescendantByName<Button>(tabControl, "AddButton");
        if (addButton is not null)
        {
            var r = ElementToWindowRect(addButton, scale);
            if (r.HasValue) passthrough.Add(r.Value);
        }

        _nonClientSource.SetRegionRects(
            NonClientRegionKind.Passthrough,
            passthrough.ToArray());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ApplyTitleBarInsets() =>
        TabbedBrowser.UpdateTitleBarInsets(
            AppWindow.TitleBar.LeftInset,
            AppWindow.TitleBar.RightInset);

    private int GetTabStripHeightPx(float scale)
    {
        var tabControl = TabbedBrowser.TabControl;
        if (tabControl.TabItems.Count > 0 &&
            tabControl.ContainerFromIndex(0) is TabViewItem first &&
            first.ActualHeight > 0)
        {
            return (int)(first.ActualHeight * scale);
        }

        return (int)(TabStripDips * scale);
    }

    /// <summary>
    /// Returns the element's bounding rect in window physical pixels,
    /// or <c>null</c> if the element hasn't been laid out yet.
    /// </summary>
    private RectInt32? ElementToWindowRect(FrameworkElement element, float scale)
    {
        if (element.ActualWidth == 0 || element.ActualHeight == 0) return null;

        var root      = Content as UIElement;
        var transform = element.TransformToVisual(root);
        var origin    = transform.TransformPoint(new Point(0, 0));

        return new RectInt32(
            (int)(origin.X      * scale),
            (int)(origin.Y      * scale),
            (int)(element.ActualWidth  * scale),
            (int)(element.ActualHeight * scale));
    }

    /// <summary>Depth-first search for the first descendant of type <typeparamref name="T"/> with the given name.</summary>
    private static T? FindDescendantByName<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var found = FindDescendantByName<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }
}
