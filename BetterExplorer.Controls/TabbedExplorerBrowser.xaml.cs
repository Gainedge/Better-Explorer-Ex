using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

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
public sealed partial class TabbedExplorerBrowser : UserControl {
  // ── Dependency Properties ─────────────────────────────────────────────────

  /// <summary>Initial path used when opening a brand-new tab.</summary>
  public static readonly DependencyProperty DefaultPathProperty =
      DependencyProperty.Register(
          nameof(DefaultPath), typeof(string), typeof(TabbedExplorerBrowser),
          new PropertyMetadata(null));

  public string? DefaultPath {
    get => (string?)GetValue(DefaultPathProperty);
    set => SetValue(DefaultPathProperty, value);
  }

  // ── Events ────────────────────────────────────────────────────────────────

  /// <summary>Raised whenever the active tab's path changes.</summary>
  public event EventHandler<string>? PathChanged;

  // ── Constructor ───────────────────────────────────────────────────────────

  public TabbedExplorerBrowser() {
    InitializeComponent();
    Loaded += OnLoaded;
  }

  private async void OnLoaded(object sender, RoutedEventArgs e) {
    // TabView hosts its tabs in an internal ListView whose default
    // ItemContainerTransitions fade/scale new items in from empty. That's why a
    // freshly added tab showed blank space for a moment even though its header
    // (icon + label) was already fully built. Clearing the transitions makes the
    // fully-rendered header appear the instant the tab is added.
    if (FindDescendant<ListView>(Tabs) is { } tabListView) {
      tabListView.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
      tabListView.Transitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
    }

    // LoadingOverlay is Visible and Tabs is Collapsed by default in XAML, so the
    // loading screen is already showing — nothing renders underneath it — before
    // any of the code below runs.

    if (SettingsPage.RestoreTabs) {
      var settings = ApplicationData.Current.LocalSettings.Values;
      if (settings.TryGetValue(SettingsPage.SessionTabPathsKey, out var raw)
          && raw is string joined
          && !string.IsNullOrEmpty(joined)) {
        var paths = joined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length > 0) {
          // Only the last (soon-to-be-active) tab actually loads its content
          // at startup — enumerating every restored tab's folder up front is
          // slow and mostly wasted, since only one tab is ever looked at
          // initially. Every tab still gets its real header (icon + label)
          // immediately; the rest defer their content load until the user
          // actually selects them (see OnTabSelectionChanged).
          TaskCompletionSource? lastTabReady = null;
          for (int i = 0; i < paths.Length; i++) {
            if (i == paths.Length - 1) {
              // RunContinuationsAsynchronously is required here: without it,
              // tcs.SetResult() below (called from PathChanged, which fires
              // mid-way through LoadDirectory/OnPathChanged) would resume this
              // method's "await lastTabReady.Task" continuation (RevealTabs())
              // synchronously and inline, hiding the overlay before the rest of
              // navigation completion (selection restore, status bar, folder
              // watcher, toolbar state) has actually run.
              var tcs = lastTabReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
              AddNewTab(path: paths[i], loadContent: true, selectOnAdd: false,
                  onContentReady: () => tcs.TrySetResult());
            } else {
              AddNewTab(path: paths[i], loadContent: false, selectOnAdd: false);
            }
          }

          if (lastTabReady is not null)
            await lastTabReady.Task;
          await RevealTabsAsync();
          return;
        }
      }
    }

    // See RunContinuationsAsynchronously comment above — same reasoning applies here.
    var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    AddNewTab(path: null, loadContent: true, selectOnAdd: false,
        onContentReady: () => readyTcs.TrySetResult());
    await readyTcs.Task;
    await RevealTabsAsync();
  }

  /// <summary>
  /// Awaits one real XAML/composition render pass (via <see cref="CompositionTarget.Rendering"/>).
  /// Used after navigation truly completes to make sure the frame with the final content
  /// (items, icons) has actually been composed before revealing the tab strip — otherwise
  /// the overlay can hide a frame or two before the visual result is actually painted.
  /// </summary>
  private static Task WaitForRenderAsync() {
    var tcs = new TaskCompletionSource();
    void OnRendering(object? sender, object args) {
      CompositionTarget.Rendering -= OnRendering;
      tcs.TrySetResult();
    }
    CompositionTarget.Rendering += OnRendering;
    return tcs.Task;
  }

  /// <summary>
  /// Selects the last tab, makes the (until now Collapsed) TabView visible, and only
  /// then hides the loading overlay — once the newly-revealed tab's content has
  /// actually settled visually.
  ///
  /// Setting <c>Tabs.Visibility = Visible</c> is the FIRST time the selected
  /// TabViewItem's content is attached to the live visual tree (TabView only realizes
  /// the content of the selected tab), so navigation that already "completed" at the
  /// data level still has to go through Loaded, measure/arrange, container
  /// realization, and icon/thumbnail paint — all of which happens live, in front of
  /// the user, once Tabs is visible. Previously the overlay was hidden in the same
  /// synchronous call, so that whole build-up played out on screen with the overlay
  /// already gone.
  ///
  /// The fix: reveal Tabs while <see cref="LoadingOverlay"/> (declared after Tabs in
  /// the same Grid, so it fully covers it) is still Visible on top, wait for several
  /// real composition frames so layout/paint has a chance to fully settle underneath
  /// the overlay, and only then collapse the overlay — the user never sees the
  /// build-up.
  /// </summary>
  private async Task RevealTabsAsync() {
    Tabs.SelectedIndex = Tabs.TabItems.Count - 1;
    Tabs.Opacity = 0f;
    Tabs.Visibility = Visibility.Visible;
    LoadingOverlay.Visibility = Visibility.Collapsed;
    //Tabs.Opacity = 1f;
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
  public void UpdateTitleBarInsets(double leftInset, double rightInset) {
    TitleBarDragLeft.Width = Math.Max(leftInset, 0);
    TitleBarDragRight.Width = Math.Max(rightInset, 0);
  }

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>Adds a new tab and navigates it to <paramref name="path"/>.</summary>
  public void AddNewTab(string? path = null) => AddNewTab(path, loadContent: true, selectOnAdd: true);

  /// <summary>
  /// Persists the current tab paths and active tab index to <c>LocalSettings</c>
  /// so they can be restored on the next launch when
  /// <see cref="SettingsPage.RestoreTabs"/> is enabled.
  /// </summary>
  public void SaveSession() {
    // A tab that was restored but never actually selected (see AddNewTab's
    // loadContent:false path) never navigated, so its ExplorerBrowser.CurrentPath
    // is still empty — its target folder lives only in TabViewItem.Tag until the
    // user opens it. Fall back to that so such tabs aren't silently dropped from
    // the saved session.
    var paths = Tabs.TabItems
        .OfType<TabViewItem>()
        .Select(t => (t.Content as ExplorerBrowser)?.CurrentPath is { Length: > 0 } current
            ? current
            : t.Tag as string)
        .Where(p => !string.IsNullOrEmpty(p))
        .ToArray();

    var settings = ApplicationData.Current.LocalSettings.Values;
    if (paths.Length == 0) {
      settings.Remove(SettingsPage.SessionTabPathsKey);
      settings.Remove(SettingsPage.SessionActiveTabIndexKey);
      return;
    }

    settings[SettingsPage.SessionTabPathsKey] = string.Join('\n', paths);
    settings[SettingsPage.SessionActiveTabIndexKey] = Math.Max(0, Tabs.SelectedIndex);
  }

  /// <summary>
  /// Adds a new tab. When <paramref name="loadContent"/> is false, the header (icon +
  /// label) is still fully built and shown immediately, but the folder itself isn't
  /// enumerated — navigation is deferred until the tab is actually selected (see
  /// OnTabSelectionChanged) via <see cref="ExplorerBrowser.ArmPendingNavigation"/>.
  /// Used to restore many tabs at startup without eagerly loading every one of them.
  /// </summary>
  private void AddNewTab(string? path, bool loadContent, bool selectOnAdd, Action? onContentReady = null) {
    var browser = new ExplorerBrowser();
    browser.PathChanged += OnBrowserPathChanged;
    browser.BusyChanged += OnBrowserBusyChanged;
    browser.SearchQueryChanged += OnBrowserSearchQueryChanged;
    browser.NavigationCompleted += this.Browser_NavigationCompleted;

    // The eventual navigation target is already known at creation time (either the
    // requested path, DefaultPath, or the C:\ fallback ShellListView lands on when
    // neither is set — see ShellListView_Loaded) — so the header's label can be
    // shown immediately instead of waiting for navigation to finish. The icon loads
    // in the background (see BuildTabHeader) since shell icon extraction is slow.
    var navPath2 = !string.IsNullOrWhiteSpace(path) ? path
                 : !string.IsNullOrWhiteSpace(DefaultPath) ? DefaultPath
                 : null;

    var tabItem = new TabViewItem {
      Header = BuildTabHeader(navPath2 ?? @"C:\"),
      Content = browser,
      IsClosable = true,
    };
    tabItem.Loaded += OnTabItemLoaded;

    if (loadContent) {
      SetTabSpinner(tabItem, busy: true);

      if (onContentReady is not null) {
        // Use NavigationCompleted, not PathChanged: PathChanged fires as soon as
        // items are visible (before selection/status-bar/watcher setup and before
        // background icon/thumbnail warming finish), which caused the loading
        // overlay to hide before the tab was actually fully rendered/navigated.
        EventHandler<string>? onReady = null;
        bool onReadyFired = false;
        onReady = (_, _) => {
          browser.NavigationCompleted -= onReady;
          // Defensive: NavigationCompleted should only fire once for the initial
          // load this handler was registered for, but guard against any spurious
          // re-entrant/duplicate raise so onContentReady (typically a
          // TaskCompletionSource.SetResult) is never invoked more than once.
          if (onReadyFired) return;
          onReadyFired = true;
          onContentReady();
        };
        browser.NavigationCompleted += onReady;
      }

      // Navigate BEFORE adding to the live visual tree so that
      // _pendingNavigatePath is set when ShellListView.Loaded fires (WinUI 3
      // fires Loaded synchronously inside TabItems.Add as the element enters
      // the tree).
      if (navPath2 is not null)
        browser.Navigate(navPath2);
      else
        onContentReady?.Invoke(); // nothing to load — already "ready"
    } else if (navPath2 is not null) {
      // Defer the actual folder load until the user selects this tab.
      // ArmPendingNavigation just records the target and suppresses
      // ShellListView's Loaded-time C:\ fallback; OnTabSelectionChanged calls
      // the real Navigate() the first time this tab becomes active.
      browser.ArmPendingNavigation(navPath2);
      tabItem.Tag = navPath2;
    }

    Tabs.TabItems.Add(tabItem);
    if (selectOnAdd)
      Tabs.SelectedItem = tabItem;

    SyncContentBackground();
  }

  private void Browser_NavigationCompleted(Object? sender, String e) {
    Tabs.Opacity = 1f;
  }

  /// <summary>
  /// Snaps the <see cref="TabViewItem"/> to its final "Selected" visual state
  /// without playing the entrance animation, so "Loading…" text is instantly visible.
  /// </summary>
  private static void OnTabItemLoaded(object sender, RoutedEventArgs e) {
    if (sender is TabViewItem tab) {
      tab.Loaded -= OnTabItemLoaded;
      // useTransitions=false skips VSM-driven opacity/offset animations.
      // "Selected" and "Normal" belong to the same mutually-exclusive VSM group,
      // so only the state matching the tab's actual IsSelected value must be
      // applied here \u2014 forcing both in sequence always left the tab snapped to
      // "Normal" regardless of selection, which is why the selected-tab highlight
      // was missing until a pointer-driven repaint (mouse move) corrected it.
      VisualStateManager.GoToState(tab, tab.IsSelected ? "Selected" : "Normal", useTransitions: false);
      VisualStateManager.GoToState(tab, "TabViewItem", useTransitions: false);
    }
  }

  /// <summary>Returns the <see cref="ExplorerBrowser"/> hosted by the currently selected tab, or null.</summary>
  public ExplorerBrowser? ActiveBrowser =>
      (Tabs.SelectedItem as TabViewItem)?.Content as ExplorerBrowser;

  // ── Tab-strip event handlers ──────────────────────────────────────────────

  private void OnAddTabButtonClick(TabView sender, object args) => AddNewTab();

  private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) {
    // Detach the path-changed handler before removing.
    if (args.Tab.Content is ExplorerBrowser browser) {
      browser.PathChanged -= OnBrowserPathChanged;
      browser.BusyChanged -= OnBrowserBusyChanged;
      browser.SearchQueryChanged -= OnBrowserSearchQueryChanged;
    }

    sender.TabItems.Remove(args.Tab);

    // Always keep at least one tab open.
    if (sender.TabItems.Count == 0)
      AddNewTab();
  }

  private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e) {
    SyncContentBackground();

    // Deactivate the outgoing tab so it saves its popup state before hiding.
    foreach (var removed in e.RemovedItems) {
      if (removed is TabViewItem { Content: ExplorerBrowser outgoing })
        outgoing.Deactivate();
    }

    // Background tabs restored at startup don't load their content until first
    // selected (see AddNewTab's loadContent:false path) — kick off the real
    // navigation now that the user is actually looking at this tab.
    if (Tabs.SelectedItem is TabViewItem { Tag: string deferredPath } selectedTab
        && selectedTab.Content is ExplorerBrowser deferredBrowser) {
      selectedTab.Tag = null;
      SetTabSpinner(selectedTab, busy: true);
      deferredBrowser.Navigate(deferredPath);
    }

    if (ActiveBrowser is { } browser) {
      browser.Activate();
      PathChanged?.Invoke(this, browser.CurrentPath ?? string.Empty);
    }
  }

  /// <summary>
  /// Reads the background of the currently selected <see cref="TabViewItem"/> and
  /// applies it to <see cref="ContentGrid"/> so the content area looks like a
  /// continuation of the active tab.
  /// </summary>
  private void SyncContentBackground() {
    // Keep the content area transparent so the Mica backdrop shows through.
    ContentGrid.Background = null;
  }

  private void OnBrowserPathChanged(object? sender, string path) {
    if (sender is not ExplorerBrowser browser)
      return;

    foreach (var obj in Tabs.TabItems) {
      if (obj is TabViewItem tab && ReferenceEquals(tab.Content, browser)) {
        // Update the existing header in place rather than tearing it down and
        // rebuilding it (which used to reset a brand-new TabIconState/spinner
        // every time PathChanged fired — including re-selecting an already-fully-
        // loaded background tab, which doesn't actually change its path). The
        // icon is only touched, and only re-fetched, when the path it represents
        // actually changed.
        UpdateTabHeader(tab, path);
        // PathChanged only fires once navigation has actually finished, so the
        // tab is idle at this point.
        SetTabSpinner(tab, busy: false);
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
  private void OnBrowserBusyChanged(object? sender, bool busy) {
    if (sender is not ExplorerBrowser browser)
      return;
    foreach (var obj in Tabs.TabItems) {
      if (obj is TabViewItem tab && ReferenceEquals(tab.Content, browser)) {
        SetTabSpinner(tab, busy);
        return;
      }
    }
  }

  /// <summary>
  /// Per-icon-slot state backing the spinner/icon visibility. The spinner must stay
  /// visible whenever EITHER the tab is busy navigating OR the background icon fetch
  /// (see <see cref="LoadTabIconAsync"/>) hasn't completed yet — otherwise a tab that
  /// finishes navigating before its icon has loaded briefly shows neither (a blank
  /// icon slot) until the icon pops in.
  /// </summary>
  private sealed class TabIconState {
    public bool Busy;
    public bool IconLoaded;
    public string? IconPath;
  }

  /// <summary>Toggles the spinner vs. folder-icon visibility inside a tab header.</summary>
  private static void SetTabSpinner(TabViewItem tab, bool busy) {
    if (tab.Header is not StackPanel sp)
      return;
    foreach (var child in sp.Children) {
      if (child is Grid iconSlot && iconSlot.Tag is TabIconState state) {
        state.Busy = busy;
        ApplyIconSlotVisibility(iconSlot, state);
        break;
      }
    }
  }

  /// <summary>Shows the spinner unless the tab is idle AND its icon has finished loading.</summary>
  private static void ApplyIconSlotVisibility(Grid iconSlot, TabIconState state) {
    bool showSpinner = state.Busy || !state.IconLoaded;
    foreach (var slotChild in iconSlot.Children) {
      if (slotChild is Image img)
        img.Visibility = showSpinner ? Visibility.Collapsed : Visibility.Visible;
      else if (slotChild is ProgressRing ring)
        ring.Visibility = showSpinner ? Visibility.Visible : Visibility.Collapsed;
    }
  }

  /// <summary>
  /// Updates the text label in the tab that hosts <paramref name="sender"/> to
  /// reflect the active search query, or restores the folder name when the
  /// search is cleared (<paramref name="query"/> is null).
  /// </summary>
  private void OnBrowserSearchQueryChanged(object? sender, string? query) {
    if (sender is not ExplorerBrowser browser)
      return;
    foreach (var obj in Tabs.TabItems) {
      if (obj is TabViewItem tab && ReferenceEquals(tab.Content, browser)) {
        if (query is not null)
          SetTabLabel(tab, $"Search: {query}");
        // null means search cleared — OnBrowserPathChanged fires next and
        // replaces the full header with the correct folder name.
        return;
      }
    }
  }

  /// <summary>Updates only the <see cref="TextBlock"/> inside a tab header StackPanel.</summary>
  private static void SetTabLabel(TabViewItem tab, string label) {
    if (tab.Header is not StackPanel sp)
      return;
    foreach (var child in sp.Children) {
      if (child is TextBlock tb) {
        tb.Text = label;
        return;
      }
    }
  }

  private static bool IsFtpPath(string path) =>
      path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
      path.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase) ||
      path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) ||
      path.StartsWith("scp://", StringComparison.OrdinalIgnoreCase);

  // Shell icon extraction (SHGetFileInfo/IExtractIcon, or the IDLIST route for virtual
  // known folders) is a synchronous COM call that can take up to ~1s on a cold shell32
  // cache — long enough to visibly freeze the UI thread if done inline. The display name,
  // by contrast, is cheap (hard-coded for common known folders; a lightweight shell call
  // otherwise). So the header is built with its real label immediately, and the icon is
  // fetched on a background thread and patched in once ready, mirroring the pattern in
  // ShellBreadcrumbBar.UpdateRowIconAsync.
  private static readonly Dictionary<string, WriteableBitmap?> _tabIconCache = new();

  private static string GetDisplayName(string path) => IsFtpPath(path)
      // Avoid slow shell calls for FTP/SFTP paths; derive a display name directly.
      ? (Uri.TryCreate(path, UriKind.Absolute, out var uri)
          ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
          : path)
      : NativeShell.GetVirtualRootDisplayName(path);

  private static StackPanel BuildTabHeader(string path) {
    string displayName = GetDisplayName(path);

    // Icon slot: a 16×16 Grid that stacks the folder icon and a spinner.
    // Only one of the two is visible at a time — driven by the attached
    // TabIconState (see ApplyIconSlotVisibility), which keeps the spinner up
    // until BOTH navigation is idle AND the background icon fetch below completes,
    // so the slot never goes blank in between.
    var spinner = new ProgressRing {
      Width = 14,
      Height = 14,
      IsIndeterminate = true,
      VerticalAlignment = VerticalAlignment.Center,
      HorizontalAlignment = HorizontalAlignment.Center,
    };

    var state = new TabIconState();
    var iconSlot = new Grid {
      Width = 16,
      Height = 16,
      VerticalAlignment = VerticalAlignment.Center,
      Tag = state,
    };
    iconSlot.Children.Add(spinner);
    ApplyIconSlotVisibility(iconSlot, state);

    var panel = new StackPanel {
      Orientation = Orientation.Horizontal,
      Spacing = 6,
      Margin = new Thickness(6, 0, 0, 0),
    };

    panel.Children.Add(iconSlot);
    panel.Children.Add(new TextBlock {
      Text = displayName,
      VerticalAlignment = VerticalAlignment.Center,
    });

    state.IconPath = path;
    if (!IsFtpPath(path))
      _ = LoadTabIconAsync(iconSlot, state, path);
    else
      state.IconLoaded = true; // FTP paths never get a shell icon — stop waiting for one.

    return panel;
  }

  /// <summary>
  /// Updates an existing tab header in place instead of rebuilding it: the label is
  /// always refreshed (cheap), but the icon slot's background fetch is only kicked off
  /// if the folder actually changed. Whatever icon is already showing (even if it's for
  /// the previous folder) is deliberately left on screen untouched here — see
  /// <see cref="LoadTabIconAsync"/>, which swaps it out once the replacement is ready.
  /// Removing it up front and waiting for the spinner to take over used to leave a
  /// blank frame whenever the fetch resolved fast enough that the spinner never got a
  /// chance to actually paint (e.g. a cache hit).
  /// </summary>
  private static void UpdateTabHeader(TabViewItem tab, string path) {
    if (tab.Header is not StackPanel sp) {
      tab.Header = BuildTabHeader(path);
      return;
    }

    Grid? iconSlot = null;
    foreach (var child in sp.Children) {
      if (child is TextBlock tb)
        tb.Text = GetDisplayName(path);
      else if (child is Grid g)
        iconSlot = g;
    }

    if (iconSlot?.Tag is not TabIconState state)
      return;
    if (string.Equals(state.IconPath, path, StringComparison.OrdinalIgnoreCase))
      return; // Same folder as before — nothing to refresh.

    state.IconPath = path;
    if (!IsFtpPath(path))
      _ = LoadTabIconAsync(iconSlot, state, path);
    else
      state.IconLoaded = true;
  }

  private static async Task LoadTabIconAsync(Grid iconSlot, TabIconState state, string path) {
    if (!_tabIconCache.TryGetValue(path, out var bmp)) {
      // Extract raw pixels on a background thread (safe — no WinRT UI objects
      // created there); WriteableBitmap itself must be created on the UI thread.
      var (pixels, w, h) = await Task.Run(() => {
        // For virtual known-folder paths like ::{GUID} the plain
        // SHCreateItemFromParsingName call inside TryGetShellHBitmap can fail;
        // use the IDLIST route instead.
        IntPtr hbm = path.StartsWith("::{", StringComparison.OrdinalIgnoreCase)
                  && Guid.TryParse(path.Trim(':', '{', '}'), out var tabFolderId)
            ? NativeShell.TryGetIDListKnownFolderHBitmap(tabFolderId, 16)
            : NativeShell.TryGetShellHBitmap(path, 16, NativeShell.SIIGBF.IconOnly);
        if (hbm == IntPtr.Zero)
          return (null, 0, 0);
        try { return NativeShell.HBitmapToPixels(hbm); } finally { NativeShell.DeleteObject(hbm); }
      });

      bmp = pixels is not null ? NativeShell.PixelsToBitmapSync(pixels, w, h) : null;
      _tabIconCache[path] = bmp;
    }

    // The icon slot may have moved on to a different folder while this fetch was in
    // flight (e.g. UpdateTabHeader reused it for a new path via rapid navigation) —
    // don't clobber it with a now-stale result.
    if (state.IconPath != path)
      return;

    if (bmp is not null) {
      Image? existing = null;
      foreach (var slotChild in iconSlot.Children)
        if (slotChild is Image img) { existing = img; break; }

      if (existing is not null) {
        // Swap the bitmap on the existing Image in place — no remove/insert, so
        // there's never a frame where the icon slot shows neither the old nor
        // the new icon.
        existing.Source = bmp;
      } else {
        iconSlot.Children.Insert(0, new Image {
          Source = bmp,
          Width = 16,
          Height = 16,
          VerticalAlignment = VerticalAlignment.Center,
        });
      }
    }

    // Whether or not a bitmap came back, the fetch is done — stop waiting for it
    // (a permanent failure just means the slot falls back to showing nothing once
    // idle, rather than spinning forever).
    state.IconLoaded = true;
    ApplyIconSlotVisibility(iconSlot, state);
  }

  private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject {
    int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
    for (int i = 0; i < count; i++) {
      var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
      if (child is T match)
        return match;
      if (FindDescendant<T>(child) is { } found)
        return found;
    }
    return null;
  }
}
