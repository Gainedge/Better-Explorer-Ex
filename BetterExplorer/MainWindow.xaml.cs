using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace BetterExplorer;

public sealed partial class MainWindow : Window
{
    // Height of the TabView tab strip in device-independent pixels.
    // WinUI 3 TabView default; used when no live measurement is available.
    private const double TabStripDips = 40;

    private InputNonClientPointerSource? _nonClientSource;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;

        // Caption buttons blend into the Mica backdrop.
        AppWindow.TitleBar.ButtonBackgroundColor         = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        AppWindow.SetIcon("BENewIcon.ico");

        _nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        TabbedBrowser.Loaded   += OnTabbedBrowserLoaded;
        SizeChanged            += OnWindowSizeChanged;
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
