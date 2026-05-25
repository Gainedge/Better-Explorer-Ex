using System;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BetterExplorer.Controls;

/// <summary>
/// A tabbed shell browser control.  Each tab hosts an independent
/// <see cref="ExplorerBrowser"/> instance.  Tabs can be:
/// <list type="bullet">
///   <item>added via the + button in the tab strip</item>
///   <item>closed via the × button on each tab</item>
///   <item>reordered by dragging</item>
///   <item>scrolled when there are too many to display</item>
/// </list>
/// </summary>
public sealed partial class TabbedExplorerBrowser : UserControl
{
    // ── Dependency Properties ─────────────────────────────────────────────────

    /// <summary>Initial path used when opening a brand-new tab.</summary>
    public static readonly DependencyProperty DefaultPathProperty =
        DependencyProperty.Register(
            nameof(DefaultPath), typeof(string), typeof(TabbedExplorerBrowser),
            new PropertyMetadata(null));

    public string? DefaultPath
    {
        get => (string?)GetValue(DefaultPathProperty);
        set => SetValue(DefaultPathProperty, value);
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised whenever the active tab's path changes.</summary>
    public event EventHandler<string>? PathChanged;

    // ── Constructor ───────────────────────────────────────────────────────────

    public TabbedExplorerBrowser()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Open the first tab automatically.
        AddNewTab();
    }

    // ── Title-bar integration ─────────────────────────────────────────────────

    /// <summary>
    /// The underlying <see cref="TabView"/> control.
    /// The host window uses this to enumerate tab items and compute
    /// the <see cref="Microsoft.UI.Input.InputNonClientPointerSource"/> regions.
    /// </summary>
    public TabView TabControl => Tabs;

    /// <summary>
    /// Updates the widths of the left/right passive drag pads to match the
    /// system-reserved insets reported by <c>AppWindowTitleBar</c>.
    /// Call this once after the window is activated and again whenever
    /// <c>AppWindow.TitleBar.Changed</c> fires (e.g. DPI change, snap layouts).
    /// </summary>
    public void UpdateTitleBarInsets(double leftInset, double rightInset)
    {
        TitleBarDragLeft.Width  = Math.Max(leftInset,  0);
        TitleBarDragRight.Width = Math.Max(rightInset, 0);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Adds a new tab and optionally navigates it to <paramref name="path"/>.</summary>
    public void AddNewTab(string? path = null)
    {
        var browser = new ExplorerBrowser();
        browser.PathChanged += OnBrowserPathChanged;

        var tabItem = new TabViewItem
        {
            Header = "New Tab",
            Content = browser,
            IsClosable = true
        };

        Tabs.TabItems.Add(tabItem);
        Tabs.SelectedItem = tabItem;

        SyncContentBackground();

        if (!string.IsNullOrWhiteSpace(path))
            browser.Navigate(path);
        else if (!string.IsNullOrWhiteSpace(DefaultPath))
            browser.Navigate(DefaultPath!);
    }

    /// <summary>Returns the <see cref="ExplorerBrowser"/> hosted by the currently selected tab, or null.</summary>
    public ExplorerBrowser? ActiveBrowser =>
        (Tabs.SelectedItem as TabViewItem)?.Content as ExplorerBrowser;

    // ── Tab-strip event handlers ──────────────────────────────────────────────

    private void OnAddTabButtonClick(TabView sender, object args) => AddNewTab();

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // Detach the path-changed handler before removing.
        if (args.Tab.Content is ExplorerBrowser browser)
            browser.PathChanged -= OnBrowserPathChanged;

        sender.TabItems.Remove(args.Tab);

        // Always keep at least one tab open.
        if (sender.TabItems.Count == 0)
            AddNewTab();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncContentBackground();

        var path = ActiveBrowser?.CurrentPath;
        if (!string.IsNullOrWhiteSpace(path))
            PathChanged?.Invoke(this, path!);
    }

    /// <summary>
    /// Reads the background of the currently selected <see cref="TabViewItem"/> and
    /// applies it to <see cref="ContentGrid"/> so the content area looks like a
    /// continuation of the active tab.
    /// </summary>
    private void SyncContentBackground()
    {
        if (Tabs.SelectedItem is TabViewItem selected)
            ContentGrid.Background = selected.Background;
        else
            ContentGrid.Background = null;
    }

    // ── Browser event forwarding ──────────────────────────────────────────────

    private void OnBrowserPathChanged(object? sender, string path)
    {
        // Update the tab header to show the folder icon + name from the shell.
        if (sender is not ExplorerBrowser browser) return;

        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem tab && ReferenceEquals(tab.Content, browser))
            {
                tab.Header = BuildTabHeader(path);
                break;
            }
        }

        // Only raise the outer event when this browser is the active one.
        if (browser == ActiveBrowser)
            PathChanged?.Invoke(this, path);
    }

    /// <summary>
    /// Builds a composite tab header containing a 16×16 shell icon followed by
    /// the folder's display name obtained from <c>IShellItem</c>.
    /// </summary>
    private static StackPanel BuildTabHeader(string path)
    {
        // ── Display name via IShellItem ───────────────────────────────────────
        string displayName = NativeShell.GetShellDisplayName(path);

        // ── 16×16 icon via IShellItemImageFactory ─────────────────────────────
        ImageSource? iconSource = null;
        IntPtr hbm = NativeShell.TryGetShellHBitmap(path, 16, NativeShell.SIIGBF.IconOnly);
        if (hbm != IntPtr.Zero)
        {
            try { iconSource = NativeShell.HBitmapToWriteableBitmap(hbm); }
            finally { NativeShell.DeleteObject(hbm); }
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        if (iconSource is not null)
        {
            panel.Children.Add(new Image
            {
                Source = iconSource,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = displayName,
            VerticalAlignment = VerticalAlignment.Center
        });

        return panel;
    }
}
