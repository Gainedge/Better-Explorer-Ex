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
        AddNewTab(path: null, revealOnLoad: true);
    }

    // ── Title-bar integration

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
    public void AddNewTab(string? path = null) => AddNewTab(path, revealOnLoad: false);

    private void AddNewTab(string? path, bool revealOnLoad)
    {
        var browser = new ExplorerBrowser();
        browser.PathChanged         += OnBrowserPathChanged;
        browser.BusyChanged         += OnBrowserBusyChanged;
        browser.SearchQueryChanged  += OnBrowserSearchQueryChanged;

        // Show the loading overlay. For the very first tab also hide the TabView itself.
        LoadingOverlay.Visibility = Visibility.Visible;
        if (revealOnLoad)
            Tabs.Opacity = 0;

        var tabItem = new TabViewItem
        {
            Header = "Loading\u2026",
            Content = browser,
            IsClosable = true,
            Visibility = Visibility.Collapsed
        };

        EventHandler<string>? onReady = null;
        onReady = (_, navPath) =>
        {
            browser.PathChanged -= onReady;
            tabItem.Header = BuildTabHeader(navPath!);
            tabItem.Visibility = Visibility.Visible;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            Tabs.Opacity = 1;
        };
        browser.PathChanged += onReady;

        // Navigate BEFORE adding to the live visual tree so that _pendingNavigatePath
        // is set when ShellListView.Loaded fires (WinUI 3 fires Loaded synchronously
        // inside TabItems.Add as the element enters the tree).
        var navPath2 = !string.IsNullOrWhiteSpace(path) ? path
                     : !string.IsNullOrWhiteSpace(DefaultPath) ? DefaultPath
                     : null;
        if (navPath2 is not null)
            browser.Navigate(navPath2);

        Tabs.TabItems.Add(tabItem);
        Tabs.SelectedItem = tabItem;

        SyncContentBackground();
    }

    /// <summary>
    /// Snaps the <see cref="TabViewItem"/> to its final "Selected" visual state
    /// without playing the entrance animation, so "Loading…" text is instantly visible.
    /// </summary>
    private static void OnTabItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TabViewItem tab)
        {
            tab.Loaded -= OnTabItemLoaded;
            // useTransitions=false skips VSM-driven opacity/offset animations.
            VisualStateManager.GoToState(tab, "Selected",    useTransitions: false);
            VisualStateManager.GoToState(tab, "Normal",      useTransitions: false);
            VisualStateManager.GoToState(tab, "TabViewItem", useTransitions: false);
        }
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
        {
            browser.PathChanged        -= OnBrowserPathChanged;
            browser.BusyChanged        -= OnBrowserBusyChanged;
            browser.SearchQueryChanged -= OnBrowserSearchQueryChanged;
        }

        sender.TabItems.Remove(args.Tab);

        // Always keep at least one tab open.
        if (sender.TabItems.Count == 0)
            AddNewTab();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncContentBackground();

        // Deactivate the outgoing tab so it saves its popup state before hiding.
        foreach (var removed in e.RemovedItems)
        {
            if (removed is TabViewItem { Content: ExplorerBrowser outgoing })
                outgoing.Deactivate();
        }

        if (ActiveBrowser is { } browser)
        {
            browser.Activate();
            PathChanged?.Invoke(this, browser.CurrentPath ?? string.Empty);
        }
    }

    /// <summary>
    /// Reads the background of the currently selected <see cref="TabViewItem"/> and
    /// applies it to <see cref="ContentGrid"/> so the content area looks like a
    /// continuation of the active tab.
    /// </summary>
    private void SyncContentBackground()
    {
        // Keep the content area transparent so the Mica backdrop shows through.
        ContentGrid.Background = null;
    }

    private void OnBrowserPathChanged(object? sender, string path)
    {
        if (sender is not ExplorerBrowser browser) return;

        var header = BuildTabHeader(path);

        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem tab && ReferenceEquals(tab.Content, browser))
            {
                tab.Header = header;
                break;
            }
        }

        // Only raise the outer event when this browser is the active one.
        if (browser == ActiveBrowser)
            PathChanged?.Invoke(this, path);
    }

    /// <summary>
    /// Shows or hides the spinner in the tab that hosts <paramref name="sender"/>.
    /// True = navigation/search in progress (spinner visible, icon hidden).
    /// False = idle (icon visible, spinner hidden).
    /// </summary>
    private void OnBrowserBusyChanged(object? sender, bool busy)
    {
        if (sender is not ExplorerBrowser browser) return;
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem tab && ReferenceEquals(tab.Content, browser))
            {
                SetTabSpinner(tab, busy);
                return;
            }
        }
    }

    /// <summary>Toggles the spinner vs. folder-icon visibility inside a tab header.</summary>
    private static void SetTabSpinner(TabViewItem tab, bool busy)
    {
        if (tab.Header is not StackPanel sp) return;
        foreach (var child in sp.Children)
        {
            if (child is Grid iconSlot)
            {
                foreach (var slotChild in iconSlot.Children)
                {
                    if (slotChild is Image img)
                        img.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
                    else if (slotChild is ProgressRing ring)
                        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Updates the text label in the tab that hosts <paramref name="sender"/> to
    /// reflect the active search query, or restores the folder name when the
    /// search is cleared (<paramref name="query"/> is null).
    /// </summary>
    private void OnBrowserSearchQueryChanged(object? sender, string? query)
    {
        if (sender is not ExplorerBrowser browser) return;
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem tab && ReferenceEquals(tab.Content, browser))
            {
                if (query is not null)
                    SetTabLabel(tab, $"Search: {query}");
                // null means search cleared — OnBrowserPathChanged fires next and
                // replaces the full header with the correct folder name.
                return;
            }
        }
    }

    /// <summary>Updates only the <see cref="TextBlock"/> inside a tab header StackPanel.</summary>
    private static void SetTabLabel(TabViewItem tab, string label)
    {
        if (tab.Header is not StackPanel sp) return;
        foreach (var child in sp.Children)
        {
            if (child is TextBlock tb)
            {
                tb.Text = label;
                return;
            }
        }
    }

    private static StackPanel BuildTabHeader(string path)
    {
        string displayName = NativeShell.GetVirtualRootDisplayName(path);
        // For virtual known-folder paths like ::{GUID} the plain SHCreateItemFromParsingName
        // call inside TryGetShellHBitmap can fail; use the IDLIST route instead.
        IntPtr hbm;
        if (path.StartsWith("::{" , StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(path.Trim(':', '{', '}'), out var tabFolderId))
            hbm = NativeShell.TryGetIDListKnownFolderHBitmap(tabFolderId, 16);
        else
            hbm = NativeShell.TryGetShellHBitmap(path, 16, NativeShell.SIIGBF.IconOnly);
        ImageSource? iconSource = null;
        if (hbm != IntPtr.Zero)
        {
            try { iconSource = NativeShell.HBitmapToWriteableBitmap(hbm); }
            finally { NativeShell.DeleteObject(hbm); }
        }

        // Icon slot: a 16×16 Grid that stacks the folder icon and a spinner.
        // Only one of the two is visible at a time (toggled by SetTabSpinner).
        var spinner = new ProgressRing
        {
            Width             = 14,
            Height            = 14,
            IsIndeterminate   = true,
            Visibility        = Visibility.Collapsed,   // hidden until nav starts
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var iconSlot = new Grid
        {
            Width  = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (iconSource is not null)
            iconSlot.Children.Add(new Image
            {
                Source            = iconSource,
                Width             = 16,
                Height            = 16,
                VerticalAlignment = VerticalAlignment.Center,
            });

        iconSlot.Children.Add(spinner);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
            Margin      = new Thickness(6, 0, 0, 0),
        };

        panel.Children.Add(iconSlot);
        panel.Children.Add(new TextBlock
        {
            Text = displayName,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }
}
