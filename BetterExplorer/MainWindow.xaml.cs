using System.Collections.Generic;
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

        _nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        RestoreWindowPlacement();

        TabbedBrowser.Loaded   += OnTabbedBrowserLoaded;
        SizeChanged            += OnWindowSizeChanged;
        Closed                 += OnWindowClosed;
    }

    // ── Window placement persistence ─────────────────────────────────────────

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

    private void OnWindowClosed(object sender, WindowEventArgs e) => SaveWindowPlacement();

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
