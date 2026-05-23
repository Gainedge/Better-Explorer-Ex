using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BetterExplorer.ShellApi;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BetterExplorer.Controls;

public sealed partial class ShellListView : UserControl {
  // ── Dependency Properties ────────────────────────────────────────────────

  public static readonly DependencyProperty ViewModeProperty =
      DependencyProperty.Register(
          nameof(ViewMode), typeof(ShellViewMode), typeof(ShellListView),
          new PropertyMetadata(ShellViewMode.SmallIcons, OnViewModeChanged));

  public static readonly DependencyProperty CanGoBackProperty =
      DependencyProperty.Register(
          nameof(CanGoBack), typeof(bool), typeof(ShellListView),
          new PropertyMetadata(false));

  public static readonly DependencyProperty CanGoForwardProperty =
      DependencyProperty.Register(
          nameof(CanGoForward), typeof(bool), typeof(ShellListView),
          new PropertyMetadata(false));

  public static readonly DependencyProperty CurrentPathProperty =
      DependencyProperty.Register(
          nameof(CurrentPath), typeof(string), typeof(ShellListView),
          new PropertyMetadata(string.Empty));

  // ── Public surface ───────────────────────────────────────────────────────

  public ShellViewMode ViewMode {
    get => (ShellViewMode)GetValue(ViewModeProperty);
    set => SetValue(ViewModeProperty, value);
  }

  public bool CanGoBack {
    get => (bool)GetValue(CanGoBackProperty);
    private set => SetValue(CanGoBackProperty, value);
  }

  public bool CanGoForward {
    get => (bool)GetValue(CanGoForwardProperty);
    private set => SetValue(CanGoForwardProperty, value);
  }

  public string CurrentPath {
    get => (string)GetValue(CurrentPathProperty);
    private set => SetValue(CurrentPathProperty, value);
  }

  public RangeObservableCollection<ShellItem> Items { get; } = new();

  /// <summary>Raised whenever the current directory changes.</summary>
  public event EventHandler<string>? PathChanged;

  /// <summary>Raised whenever sort column or direction changes (column-header tap or toolbar).</summary>
  public event EventHandler? SortChanged;

  public string SortColumn    => _sortColumn;
  public bool   SortAscending => _sortAscending;

  // ── Private state ────────────────────────────────────────────────────────

  private readonly Stack<string> _backStack = new();
  private readonly Stack<string> _forwardStack = new();
  // Paths that LoadDirectory should select after it finishes populating the list.
  // Set by GoUp (select the folder we came from) and Refresh (reselect previous items).
  private List<string> _pendingSelectPaths = [];
  // Cancels any in-flight WaitForContainerThenUpdatePopupAsync when a new navigation starts.
  private CancellationTokenSource _popupWaitCts = new();
  private CancellationTokenSource _thumbCts = new();
  // Separate CTS for in-flight LoadDirectory / NavigateToKnownFolder work so that
  // restarting the thumbnail worker does not cancel an ongoing navigation.
  private CancellationTokenSource _navCts = new();
  private const int ThumbConcurrency = 8;
  private const int CloudThumbRetryMax = 16;   // ~40 s total with progressive back-off
  private const int CloudThumbRetryBaseMs = 500;  // delay = min(retry * 500, 4000)
  private const int CloudThumbRetryMaxMs = 4000;
  private Channel<(ShellItem Item, uint Size, int Retry)> _thumbChannel = CreateChannel();

  private static readonly Dictionary<(string Ext, uint Size), WriteableBitmap> _typeIconCache = new();

  // Per-item thumbnail cache for cloud/Storage-API items — persists across navigation
  // so scroll-back is instant without hitting the Windows.Storage broker again.
  private const int StorageApiConcurrency = 32;
  private static readonly SemaphoreSlim _storageApiSem =
      new SemaphoreSlim(StorageApiConcurrency, StorageApiConcurrency);
  private static readonly ConcurrentDictionary<(string Path, uint Size), WriteableBitmap> _thumbCache = new();
  private const int ThumbCacheMaxSize = 600;

  private static readonly HashSet<string> _thumbnailExts = new(StringComparer.OrdinalIgnoreCase)
  {
        ".jpg",".jpeg",".png",".gif",".bmp",".tiff",".tif",".webp",".heic",".heif",
        ".raw",".cr2",".nef",".arw",".dng",
        ".mp4",".avi",".mkv",".mov",".wmv",".m4v",".flv",".webm",
        ".pdf"
    };

  private static readonly HashSet<string> _perFileIconExts = new(StringComparer.OrdinalIgnoreCase)
  {
        ".exe", ".dll", ".lnk", ".ico", ".cpl"
    };

  private static Channel<(ShellItem, uint, int)> CreateChannel() =>
      Channel.CreateBounded<(ShellItem, uint, int)>(new BoundedChannelOptions(512) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false
      });

  // ── Details column resize state ──────────────────────────────────────────

  private DetailsColumnSettings DetailsColumns =>
      (DetailsColumnSettings)Resources["DetailsColumns"];
  private Border? _activeGripper;
  private double _gripperStartX;
  private double _gripperStartWidth;

  // ── Sort state ────────────────────────────────────────────────────────────

  private string _sortColumn = "Name";
  private bool _sortAscending = true;
  private ShellViewMode _currentMode = ShellViewMode.Details;
  // True while LoadDirectory/NavigateToKnownFolder is applying loaded settings
  // so that property-change callbacks do not trigger a redundant save.
  private bool _applyingFolderSettings;

  // ── Column drag-reorder state ─────────────────────────────────────────────

  private int     _colDragSourceIndex = -1;   // index in Columns of the dragged column
  private bool    _colDragging        = false; // true once drag threshold exceeded
  private double  _colDragStartX      = 0;     // pointer X at press, relative to DetailsHeader
  private Grid?   _colDragCell        = null;  // the header Grid cell being dragged
  private double  _colDragPopupOffsetX = 0;    // pointer X within the dragged cell at press time

  // ── Clipboard state ──────────────────────────────────────────────────────

  // Paths and operation stored by Ctrl+C / Ctrl+X so paste works even after
  // navigating away (WinRT broker access can fail for arbitrary paths).
  private List<string>? _clipboardPaths;
  private bool _clipboardIsCut;

  // Subset of _clipboardPaths that are still visible in the current view —
  // used only to clear the IsCut ghost visual on the ShellItem models.
  private List<string>? _clipboardCutPaths;

  // ── Constructor ──────────────────────────────────────────────────────────

  public ShellListView() {
    InitializeComponent();
    ShellView.ItemsSource = Items;
    ShellView.ContainerContentChanging += OnContainerContentChanging;
    ShellView.SelectionChanged += OnShellViewSelectionChanged;
    ApplyViewMode(ViewMode);
  }

  private void ShellListView_Loaded(object sender, RoutedEventArgs e) {
    if (string.IsNullOrEmpty(CurrentPath))
      Navigate(@"C:\");

    DragSelectGrid.AddHandler(PointerPressedEvent,
        new PointerEventHandler(OnDragSelectPointerPressed), true);
    DragSelectGrid.AddHandler(PointerMovedEvent,
        new PointerEventHandler(OnDragSelectPointerMoved), true);
    DragSelectGrid.AddHandler(PointerReleasedEvent,
        new PointerEventHandler(OnDragSelectPointerReleased), true);
    DragSelectGrid.AddHandler(PointerCaptureLostEvent,
        new PointerEventHandler(OnDragSelectPointerCaptureLost), true);
    DragSelectGrid.AddHandler(PointerCanceledEvent,
        new PointerEventHandler(OnDragSelectPointerCaptureLost), true);

    // Subscribe to scroll-view thumbnail enqueueing now, not lazily on first drag.
    if (_scrollViewer is null) {
      _scrollViewer = FindDescendant<ScrollViewer>(ShellView);
      if (_scrollViewer is not null)
        _scrollViewer.ViewChanged += OnScrollViewerViewChanged;
    }

    // Register on the window-root element so Ctrl+C/X/V are caught
    // regardless of which control has focus (address bar, tree, buttons, etc.).
    if (XamlRoot?.Content is UIElement root)
      root.AddHandler(KeyDownEvent,
          new KeyEventHandler(OnShellViewKeyDown), handledEventsToo: true);

    // Reposition the name-expansion popup whenever the list area is resized
    // (e.g. window resize, pane splitter drag) so it tracks the selected item.
    DragSelectGrid.SizeChanged += OnDragSelectGridSizeChanged;
  }

  private void ShellListView_Unloaded(object sender, RoutedEventArgs e) {
    if (XamlRoot?.Content is UIElement root)
      root.RemoveHandler(KeyDownEvent,
          new KeyEventHandler(OnShellViewKeyDown));

    DragSelectGrid.SizeChanged -= OnDragSelectGridSizeChanged;
  }

  private void OnDragSelectGridSizeChanged(object sender, SizeChangedEventArgs e) {
    // If the popup is open, recompute its position against the item's new screen
    // coordinates — the item may have shifted during a window/pane resize.
    if (NameExpansionPopup.IsOpen)
      UpdateNameExpansion();
  }

  // ── Navigation API ───────────────────────────────────────────────────────

  public void Navigate(string path) {
    // Normalise: strip surrounding quotes and expand environment variables so that
    // typed paths like %USERPROFILE%\Documents or "C:\Foo" work from the address bar.
    path = path.Trim().Trim('"');
    path = Environment.ExpandEnvironmentVariables(path);

    if (!Directory.Exists(path))
      return;

    // Don't re-navigate to the folder already shown — use Refresh() for that.
    if (string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
      return;

    if (!string.IsNullOrEmpty(CurrentPath))
      _backStack.Push(CurrentPath);
    _forwardStack.Clear();
    LoadDirectory(path);
  }

  public void GoBack() {
    if (_backStack.Count == 0)
      return;
    var leaving = CurrentPath;
    _forwardStack.Push(leaving);
    var dest = _backStack.Pop();
    // If the destination is an ancestor of where we are, pre-select the child
    // folder we came from so the user sees where they were.
    if (IsDirectChild(dest, leaving))
      _pendingSelectPaths = [leaving.TrimEnd('\\', '/')];
    LoadDirectory(dest);
  }

  public void GoForward() {
    if (_forwardStack.Count == 0)
      return;
    var leaving = CurrentPath;
    _backStack.Push(leaving);
    var dest = _forwardStack.Pop();
    // Pre-select the child we're going back into when returning to a subfolder.
    if (IsDirectChild(leaving, dest))
      _pendingSelectPaths = [dest.TrimEnd('\\', '/')];
    LoadDirectory(dest);
  }

  public void GoUp() {
    var fromPath = CurrentPath.TrimEnd('\\', '/');
    var (fsPath, knownFolderGuid) = NativeShell.TryGetShellParent(CurrentPath);
    if (fsPath is not null) {
      _pendingSelectPaths = [fromPath];
      Navigate(fsPath);
    } else if (knownFolderGuid != Guid.Empty) {
      _pendingSelectPaths = [fromPath];
      NavigateToKnownFolder(knownFolderGuid);
    }
  }

  /// <summary>Returns true when <paramref name="child"/> is a direct or indirect
  /// descendant of <paramref name="parent"/>.</summary>
  private static bool IsDirectChild(string parent, string child) {
    if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child))
      return false;
    var p = parent.TrimEnd('\\', '/');
    var c = child.TrimEnd('\\', '/');
    return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || c.StartsWith(p + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
  }

  public void Refresh() {
    if (string.IsNullOrEmpty(CurrentPath))
      return;
    // Collapse the expansion popup immediately so it doesn't stay frozen at the
    // old item position while the list reloads and items shift around.
    CollapseAllNameExpansions();
    // Remember which items are currently selected so they can be restored.
    _pendingSelectPaths = ShellView.SelectedItems
        .OfType<ShellItem>()
        .Select(i => i.FullPath)
        .ToList();
    LoadDirectory(CurrentPath);
  }

  /// <summary>
  /// Moves keyboard focus onto the inner ListView so selected items render in
  /// the <c>Selected</c> visual state (accent colour) rather than the dimmer
  /// <c>SelectedUnfocused</c> state.
  /// </summary>
  public void FocusListView() => ShellView.Focus(FocusState.Programmatic);

  public async void NavigateToKnownFolder(Guid folderId) {
    var virtualPath = $"::{folderId:B}";
    await ApplyFolderSettings(virtualPath);

    // Don't re-navigate to the folder already shown
    if (string.Equals(virtualPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
      return;

    if (!string.IsNullOrEmpty(CurrentPath))
      _backStack.Push(CurrentPath);
    _forwardStack.Clear();

    // Cancel any pending popup-wait from the previous navigation.
    _popupWaitCts.Cancel();
    _popupWaitCts = new CancellationTokenSource();

    // Cancel any previous navigation and start a fresh one.
    _navCts.Cancel();
    _navCts = new CancellationTokenSource();
    var ct = _navCts.Token;

    var items = NativeShell.EnumerateKnownFolderChildren(folderId);

    RestartThumbnailWorker();
    Items.Clear();

    if (items.Count > 0) {
      // Sort items before processing icons/thumbnails.
      items = SortItems(items);

      var size = ThumbnailSizeForMode(ViewMode);

      try {
        await WarmTypeIconCacheAsync(items, size, ct);
      } catch (OperationCanceledException) { return; }
      if (ct.IsCancellationRequested)
        return;

      ApplyCachedIcons(items, size);

      int viewportCount = Math.Min(items.Count, EstimateViewportItemCount(ViewMode));
      if (viewportCount > 0) {
        var viewportSlice = viewportCount == items.Count
            ? items
            : items.GetRange(0, viewportCount);
        try {
          await PreloadCachedThumbnailsAsync(viewportSlice, size, ct);
        } catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested)
          return;
      }

      Items.AddRange(items);
    }

    // Update CurrentPath and fire PathChanged regardless of whether items were found.
    CurrentPath = virtualPath;
    PathChanged?.Invoke(this, CurrentPath);
    CanGoBack = _backStack.Count > 0;
    CanGoForward = _forwardStack.Count > 0;
    ApplyPendingSelection();
  }

  // ── Directory loading ────────────────────────────────────────────────────

  private async void LoadDirectory(string path) {
    await ApplyFolderSettings(path);

    // Reset selection on outgoing items
    // never reference a ShellItem with IsSelected=true during the next layout pass.
    foreach (var item in Items)
      item.IsSelected = false;

    // Clear cut-ghost state — the items are leaving the view.
    ClearCutGhosts();

    // Cancel any pending popup-wait from the previous navigation so it doesn't
    // race with the new one and open the popup at a stale position.
    _popupWaitCts.Cancel();
    _popupWaitCts = new CancellationTokenSource();

    // Cancel any previous navigation and start a fresh one.
    _navCts.Cancel();
    _navCts = new CancellationTokenSource();
    var ct = _navCts.Token;

    RestartThumbnailWorker();
    Items.Clear();

    var size = ThumbnailSizeForMode(ViewMode);

    // Known-folder paths written by NavigateToKnownFolder (::{FOLDERID}) must be
    // re-enumerated via the shell API, not FindFirstFileEx.
    if (path.StartsWith("::", StringComparison.Ordinal)
        && Guid.TryParse(path.Trim(':', '{', '}'), out var folderId)) {
      var kfItems = await Task.Run(() => NativeShell.EnumerateKnownFolderChildren(folderId), ct);
      if (ct.IsCancellationRequested)
        return;

      // Sort items before processing icons/thumbnails.
      kfItems = SortItems(kfItems);

      try {
        await WarmTypeIconCacheAsync(kfItems, size, ct);
      } catch (OperationCanceledException) { return; }
      if (ct.IsCancellationRequested)
        return;
      ApplyCachedIcons(kfItems, size);

      int kfViewport = Math.Min(kfItems.Count, EstimateViewportItemCount(ViewMode));
      if (kfViewport > 0) {
        var slice = kfViewport == kfItems.Count ? kfItems : kfItems.GetRange(0, kfViewport);
        try {
          await PreloadCachedThumbnailsAsync(slice, size, ct);
        } catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested)
          return;
      }

      Items.AddRange(kfItems);
      CurrentPath = path;
      CanGoBack = _backStack.Count > 0;
      CanGoForward = _forwardStack.Count > 0;
      PathChanged?.Invoke(this, path);
      UpdateSortIndicators();
      ApplyPendingSelection();
      return;
    }

    List<ShellItem> folders = [];
    List<ShellItem> files = [];
    try {
      (folders, files) = await Task.Run(
          () => NativeShell.EnumerateWithFindFirstFileEx(path, ct), ct);
    } catch (OperationCanceledException) { return; } catch (UnauthorizedAccessException) { } catch (IOException) { }

    if (ct.IsCancellationRequested)
      return;

    var allItems = new List<ShellItem>(folders.Count + files.Count);
    allItems.AddRange(folders);
    allItems.AddRange(files);
    allItems = SortItems(allItems);

    // ── Warm type-icon cache before showing items (off-thread, parallel) ──────
    // Fills _typeIconCache for every unique extension so ApplyCachedIcons can stamp
    // every single item synchronously. Items arrive pre-stamped in one AddRange call
    // → no two-stage blink and no blank items anywhere in the list.
    try {
      await WarmTypeIconCacheAsync(allItems, size, ct);
    } catch (OperationCanceledException) { return; }
    if (ct.IsCancellationRequested)
      return;

    ApplyCachedIcons(allItems, size);

    // ── Preload cached thumbnails for the visible viewport before showing items ──
    // Awaiting only the viewport slice (not the whole list) keeps navigation snappy
    // for large folders while still preventing icon→thumbnail flicker for the items
    // the user sees first. Off-screen items get their thumbnails on demand via
    // OnContainerContentChanging (TryGetCachedPixels) as the user scrolls.
    int viewportCount = Math.Min(allItems.Count, EstimateViewportItemCount(ViewMode));
    if (viewportCount > 0) {
      var viewportSlice = viewportCount == allItems.Count
          ? allItems
          : allItems.GetRange(0, viewportCount);
      try {
        await PreloadCachedThumbnailsAsync(viewportSlice, size, ct);
      } catch (OperationCanceledException) { return; }
      if (ct.IsCancellationRequested)
        return;
    }

    // ── Show ALL items at once ────────────────────────────────────────────────
    Items.AddRange(allItems);
    CurrentPath = path;
    CanGoBack = _backStack.Count > 0;
    CanGoForward = _forwardStack.Count > 0;
    PathChanged?.Invoke(this, path);
    UpdateSortIndicators();
    ApplyPendingSelection();
  }

  /// <summary>
  /// If <see cref="_pendingSelectPaths"/> was populated before this navigation,
  /// selects the matching items in the new list and scrolls the first one into view.
  /// Clears the pending set after use so normal navigations are unaffected.
  /// </summary>
  private void ApplyPendingSelection() {
    if (_pendingSelectPaths.Count == 0)
      return;

    var targets = _pendingSelectPaths;
    _pendingSelectPaths = [];

    // Normalize pending paths — strip trailing slashes so they match FullPath
    // values produced by Path.Combine (which never adds a trailing separator).
    var set = new HashSet<string>(
        targets.Select(p => p.TrimEnd('\\', '/')),
        StringComparer.OrdinalIgnoreCase);

    // Suppress OnShellViewSelectionChanged while mutating the selection so that
    // UpdateNameExpansion is NOT triggered before containers are realized.
    _suppressSelectionChanged = true;
    ShellItem? first = null;
    try {
      foreach (var item in Items) {
        if (!set.Contains(item.FullPath.TrimEnd('\\', '/')))
          continue;
        item.IsSelected = true;
        ShellView.SelectedItems.Add(item);
        first ??= item;
      }
      if (first is not null)
        ShellView.ScrollIntoView(first);
    } finally {
      _suppressSelectionChanged = false;
    }

    if (first is not null)
      WaitForContainerThenUpdatePopupAsync(first, _popupWaitCts.Token);
  }

  /// <summary>
  /// Polls until the ListView has realized and laid out the container for
  /// <paramref name="item"/>, then opens the name-expansion popup at the
  /// correct position.  Bails out after a reasonable timeout.
  /// </summary>
  private async void WaitForContainerThenUpdatePopupAsync(ShellItem item, CancellationToken ct) {
    const int MaxAttempts = 20;
    const int DelayMs = 50;

    // The target path — used to re-locate the item after Items.Clear() replaces
    // all ShellItem instances with new ones (the old reference becomes stale).
    var targetPath = item.FullPath;

    // Initial wait — give the list time to load items and perform its first
    // full layout pass before we start probing for the container.
    try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }

    for (int i = 0; i < MaxAttempts; i++) {
      if (ct.IsCancellationRequested) return;
      try { await Task.Delay(DelayMs, ct); } catch (OperationCanceledException) { return; }

      // Re-resolve the current ShellItem by path — the original reference is
      // stale after a navigation/refresh that called Items.Clear().
      var current = Items.FirstOrDefault(
          x => string.Equals(x.FullPath, targetPath, StringComparison.OrdinalIgnoreCase));

      // Bail out if the item no longer exists or is no longer selected.
      if (current is null || !ShellView.SelectedItems.Contains(current))
        return;

      if (ShellView.ContainerFromItem(current) is ListViewItem container
          && container.ActualWidth > 0
          && container.ActualHeight > 0) {
        UpdateNameExpansion();
        return;
      }
    }
  }


  /// <summary>
  /// Synchronously stamps already-cached type icons onto items before they are
  /// added to the collection, so the first rendered frame shows icons.
  /// </summary>
  private void ApplyCachedIcons(List<ShellItem> items, uint size) {
    foreach (var item in items) {
      if (item.HasRealThumbnail || item.Icon != null)
        continue;
      var ext = item.IsFolder ? ":folder" : Path.GetExtension(item.FullPath).ToLowerInvariant();
      if (_typeIconCache.TryGetValue((ext, size), out var icon))
        item.Icon = icon;
      else {
        var fallback = FindAnyCachedIcon(ext);
        if (fallback != null)
          item.Icon = fallback;
      }
    }
  }

  /// <summary>
  /// Fills <see cref="_typeIconCache"/> for every unique extension in
  /// <paramref name="allItems"/> using a parallel off-thread HBITMAP fetch.
  /// Does NOT stamp items — call <see cref="ApplyCachedIcons"/> afterwards.
  /// </summary>
  private async Task WarmTypeIconCacheAsync(
      List<ShellItem> allItems, uint size, CancellationToken ct) {
    // Collect unique extensions not yet cached (skip per-file-icon types like .exe).
    var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in allItems) {
      var ext = item.IsFolder ? ":folder" : Path.GetExtension(item.FullPath).ToLowerInvariant();
      if (_perFileIconExts.Contains(ext))
        continue;
      if (_typeIconCache.ContainsKey((ext, size)))
        continue;
      exts.Add(ext);
    }

    if (exts.Count == 0 || ct.IsCancellationRequested)
      return;

    var keys = exts.ToArray();
    var paths = allItems.Select(s => s.FullPath).ToArray();
    var pixels = new (byte[]? Px, int W, int H)[keys.Length];

    await Task.Run(() => Parallel.For(
        0, paths.Length,
        new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
        i => {
          if (ct.IsCancellationRequested)
            return;
          var path = paths[i];
          // Derive the extension key exactly the same way it was added to `keys`
          // so that folder items map to ":folder" and never to a file extension.
          var item = allItems[i];
          var extKey = item.IsFolder
              ? ":folder"
              : Path.GetExtension(path).ToLowerInvariant();
          var pixelsIndex = keys.IndexOf(extKey);
          // Negative means this extension was already cached or is a per-file-icon
          // type that was never added to `keys` — skip rather than clamping to 0.
          if (pixelsIndex < 0)
            return;
          if (pixels[pixelsIndex].Px != null)
            return;
          var hbm = NativeShell.TryGetShellHBitmap(path, size, NativeShell.SIIGBF.IconOnly);
          if (hbm == IntPtr.Zero)
            return;
          try { pixels[pixelsIndex] = NativeShell.HBitmapToPixels(hbm); } finally { NativeShell.DeleteObject(hbm); }
        }), ct);

    if (ct.IsCancellationRequested)
      return;

    for (int i = 0; i < keys.Length; i++) {
      var (px, w, h) = pixels[i];
      if (px == null)
        continue;
      var wb = NativeShell.PixelsToBitmapSync(px, w, h);
      if (wb == null)
        continue;
      _typeIconCache[(keys[i], size)] = wb;
    }
  }

  /// <summary>
  /// Returns every <see cref="ShellItem"/> in <see cref="Items"/> whose ListView
  /// container is currently realized AND overlaps the visible scroll viewport.
  /// </summary>
  private IEnumerable<ShellItem> GetViewportVisibleItems() {
    _scrollViewer ??= FindDescendant<ScrollViewer>(ShellView);
    double top = _scrollViewer?.VerticalOffset ?? 0;
    double bottom = top + (ShellView.ActualHeight > 0 ? ShellView.ActualHeight : double.MaxValue);

    // Iterate only realized (virtualized) containers — O(realized ~20-40)
    // rather than calling ContainerFromItem for every item in the collection O(N²).
    if (ShellView.ItemsPanelRoot is not Panel panel)
      yield break;
    foreach (var child in panel.Children) {
      if (child is not ListViewItem container)
        continue;
      if (container.Content is not ShellItem item)
        continue;

      var transform = container.TransformToVisual(ShellView);
      var pos = transform.TransformPoint(new Point(0, 0));
      double itemTop = top + pos.Y;
      double itemBottom = itemTop + container.ActualHeight;

      if (itemBottom >= top && itemTop <= bottom)
        yield return item;
    }
  }

  private void EnqueueThumbnailsForItems(IEnumerable<ShellItem> items, uint size) {
    foreach (var item in items) {
      // Only enqueue if the item has a realized container — off-screen items will be
      // picked up by ContainerContentChanging when they scroll into view.
      if (item.HasRealThumbnail)
        continue;
      if (ShellView.ContainerFromItem(item) == null)
        continue;
      _thumbChannel.Writer.TryWrite((item, size, 0));
    }
  }

  /// <summary>
  /// Fired when the user scrolls. Enqueues thumbnail work for any newly visible items
  /// that do not yet have a real thumbnail, so offscreen items are loaded on demand.
  /// </summary>
  private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) {
    // Close the name-expansion popup immediately when scrolling starts.
    CollapseAllNameExpansions();

    // Skip intermediate animation frames — only enqueue thumbnails once scrolling settles.
    if (e.IsIntermediate)
      return;
    if (_thumbCts.IsCancellationRequested)
      return;
    var size = ThumbnailSizeForMode(ViewMode);
    var ct = _thumbCts.Token;

    foreach (var item in GetViewportVisibleItems()) {
      if (item.HasRealThumbnail)
        continue;
      var ext = item.IsFolder ? ":folder" : Path.GetExtension(item.FullPath).ToLowerInvariant();
      bool canUpgrade = !IsIconOnlyMode(ViewMode) &&
                        (item.IsFolder || _thumbnailExts.Contains(ext));
      if (!canUpgrade && !_perFileIconExts.Contains(ext))
        continue;
      _thumbChannel.Writer.TryWrite((item, size, 0));
    }
  }

  private const int CachePreloadConcurrency = 32;

  /// <summary>
  /// Probes the shell thumbnail disk-cache in parallel for every thumbnail-eligible
  /// item in <paramref name="items"/> and stamps the decoded bitmap directly onto
  /// <see cref="ShellItem.Icon"/> / <see cref="ShellItem.HasRealThumbnail"/> before
  /// the items are added to the visible collection.
  /// <para>
  /// The heavy work (GDI + COM cache probes, pixel copies) runs in parallel on
  /// thread-pool threads via <see cref="NativeShell.TryGetCachedPixelsBatch"/>.
  /// <see cref="NativeShell.PixelsToBitmapSync"/> (WriteableBitmap creation) is
  /// marshalled back to the UI thread.
  /// </para>
  /// </summary>
  private async Task PreloadCachedThumbnailsAsync(
      List<ShellItem> items, uint size, CancellationToken ct) {
    if (IsIconOnlyMode(ViewMode) || items.Count == 0 || ct.IsCancellationRequested)
      return;

    // Build a parallel-indexed list of paths for eligible items only.
    // Non-eligible slots are left null so TryGetCachedPixelsBatch skips them.
    var paths = new string?[items.Count];
    for (int i = 0; i < items.Count; i++) {
      var item = items[i];
      if (item.HasRealThumbnail)
        continue;
      var ext = item.IsFolder ? ":folder" : Path.GetExtension(item.FullPath).ToLowerInvariant();
      if (_perFileIconExts.Contains(ext))
        continue;
      if (!item.IsFolder && !_thumbnailExts.Contains(ext))
        continue;
      paths[i] = item.FullPath;
    }

    // Off-thread: probe the shell cache for all eligible paths in parallel.
    if (ct.IsCancellationRequested)
      return;

    (byte[]? Pixels, int W, int H)[] raw;
    try {
      raw = await Task.Run(
          () => NativeShell.TryGetCachedPixelsBatch(paths, size, CachePreloadConcurrency, ct), ct)
          .ConfigureAwait(true);   // resume on UI thread
    } catch (OperationCanceledException) {
      return;
    }

    if (ct.IsCancellationRequested)
      return;

    // UI thread: materialise bitmaps and stamp items.
    for (int i = 0; i < items.Count; i++) {
      var (px, w, h) = raw[i];
      if (px == null)
        continue;
      var wb = NativeShell.PixelsToBitmapSync(px, w, h);
      if (wb == null)
        continue;
      items[i].Icon = wb;
      items[i].HasRealThumbnail = true;
    }
  }

  private WriteableBitmap? FindAnyCachedIcon(string ext) {
    foreach (var kv in _typeIconCache)
      if (kv.Key.Ext == ext)
        return kv.Value;
    return null;
  }

  private async Task LoadTypeIconAsync(ShellItem rep, string ext, uint size, CancellationToken ct) {
    try {
      var wb = await NativeShell.GetShellImageAsync(rep.FullPath, size, NativeShell.SIIGBF.IconOnly, ct);
      if (wb != null && !ct.IsCancellationRequested)
        _typeIconCache[(ext, size)] = wb;
    } catch (OperationCanceledException) { } catch { }
  }

  // ── Selection sync ────────────────────────────────────────────────────────

  private void OnShellViewSelectionChanged(object sender, SelectionChangedEventArgs e) {
    if (_suppressSelectionChanged)
      return;
    foreach (var item in e.RemovedItems.OfType<ShellItem>()) {
      item.IsSelected = false;
      if (ShellView.ContainerFromItem(item) is ListViewItem c)
        SetContainerSelectionBorder(c, false);
    }
    foreach (var item in e.AddedItems.OfType<ShellItem>()) {
      item.IsSelected = true;
      if (ShellView.ContainerFromItem(item) is ListViewItem c)
        SetContainerSelectionBorder(c, true);
    }

    UpdateNameExpansion();
  }

  // How many px of the popup card sit inside the item's own bounds (label row height
  // = 47.5px + cpPad 4px = 51.5px total from popup top to item bottom).
  private const double NameExpansionInBoundsHeight = 47;

  private void OnNameExpansionCardSizeChanged(object sender, SizeChangedEventArgs e) {
    // Keep the overflow fills in sync if the card height changes after opening
    // (e.g. window resize or font scaling).
    double overflow = e.NewSize.Height - NameExpansionInBoundsHeight;
    NameExpansionOverflowFill.Height = Math.Max(0, overflow);
    NameExpansionOverflowFill2.Height = Math.Max(0, overflow);
  }

  // Tracks which item currently has its label hidden so we can restore it on collapse.
  private ShellItem? _expandedItem;
  private ListViewItem? _expandedContainer;

  /// <summary>
  /// In icon modes (ExtraLarge / Large / Medium), shows the shared popup below the
  /// selected item's icon so its full name is visible and can overlap items below.
  /// The in-template label is hidden to avoid double text.
  /// The popup is styled to look like the bottom portion of the selection border.
  /// </summary>
  private void UpdateNameExpansion() {
    if (!IsIconLabelExpandMode(ViewMode)) {
      CollapseAllNameExpansions();
      return;
    }

    if (ShellView.SelectedItems.Count != 1 ||
        ShellView.SelectedItems[0] is not ShellItem selected) {
      CollapseAllNameExpansions();
      return;
    }

    if (ShellView.ContainerFromItem(selected) is not ListViewItem container) {
      CollapseAllNameExpansions();
      return;
    }

    // Icon row height for the current mode — matches the DataTemplate row heights.
    double iconRowHeight = ViewMode switch {
      ShellViewMode.ExtraLargeIcons => 256,
      ShellViewMode.LargeIcons => 128,
      ShellViewMode.MediumIcons => 96,
      _ => 48
    };

    // The SelectionBorder is a child of RootGrid which has Padding="6" on all sides.
    // It is therefore inset from the container's outer edge by the padding amount.
    double padLeft = container.Padding.Left;
    double padTop = container.Padding.Top;
    double padRight = container.Padding.Right;

    // ContentPresenter inside RootGrid also has Padding="4" — this shifts the DataTemplate
    // content down by cpPad relative to the RootGrid inner origin.
    const double cpPad = 4;

    // Popup width must exactly match the SelectionBorder width = container width minus H padding.
    double popupWidth = container.ActualWidth - padLeft - padRight;

    // Container hasn't been laid out yet — bail out; the popup will be
    // positioned correctly once ActualWidth is available.
    if (popupWidth <= 0) {
      CollapseAllNameExpansions();
      return;
    }

    // Transform the top-left corner of the container into DragSelectGrid coordinates.
    var transform = container.TransformToVisual(DragSelectGrid);
    var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

    // Popup left aligns with the SelectionBorder left edge (origin + padLeft).
    // Popup top = where the label row starts = origin + padTop + cpPad + iconRowHeight.
    double popupLeft = origin.X + padLeft;
    double popupTop = origin.Y + padTop + cpPad + iconRowHeight;

    // Collapse previous expanded item if switching selection.
    if (_expandedContainer != null && _expandedContainer != container)
      VisualStateManager.GoToState(_expandedContainer, "NameCollapsed", false);
    if (_expandedItem != null && _expandedItem != selected)
      _expandedItem.IsLabelHidden = false;

    _expandedItem = selected;
    _expandedContainer = container;
    selected.IsLabelHidden = true;
    // Remove bottom border+corners of the SelectionBorder so the popup appears seamless.
    VisualStateManager.GoToState(container, "NameExpanded", false);

    NameExpansionText.Text = selected.Name;
    NameExpansionCard.Width = popupWidth;

    // Force a measure pass with the final width so the card knows its exact height
    // before it becomes visible.  This lets us set the overflow fills to their
    // final values now, so the popup opens at its definitive size with no
    // downward-expansion glitch.
    NameExpansionCard.Measure(new Windows.Foundation.Size(popupWidth, double.PositiveInfinity));
    double measuredHeight = NameExpansionCard.DesiredSize.Height;
    double overflow = measuredHeight - NameExpansionInBoundsHeight;
    NameExpansionOverflowFill.Height = Math.Max(0, overflow);
    NameExpansionOverflowFill2.Height = Math.Max(0, overflow);

    NameExpansionPopup.HorizontalOffset = popupLeft;
    NameExpansionPopup.VerticalOffset = popupTop;
    NameExpansionPopup.IsOpen = true;
  }

  private void CollapseAllNameExpansions() {
    NameExpansionPopup.IsOpen = false;
    if (_expandedContainer != null) {
      VisualStateManager.GoToState(_expandedContainer, "NameCollapsed", false);
      _expandedContainer = null;
    }
    if (_expandedItem != null) {
      _expandedItem.IsLabelHidden = false;
      _expandedItem = null;
    }
  }

  /// <summary>True for the three icon-grid modes whose labels can expand on selection.</summary>
  private static bool IsIconLabelExpandMode(ShellViewMode mode) =>
      mode is ShellViewMode.ExtraLargeIcons or ShellViewMode.LargeIcons or ShellViewMode.MediumIcons or ShellViewMode.SmallIcons;

  // ── ContainerContentChanging ─────────────────────────────────────────────

  private static void SetContainerSelectionBorder(FrameworkElement container, bool visible) {
    VisualStateManager.GoToState(
        container as Control ?? (Control)(object)container,
        visible ? "Selected" : "Unselected",
        useTransitions: false);
  }

  private static void SetContainerDropTarget(FrameworkElement container, bool visible) {
    VisualStateManager.GoToState(
        container as Control ?? (Control)(object)container,
        visible ? "DropTarget" : "NotDropTarget",
        useTransitions: false);
  }

  private void OnContainerContentChanging(
      ListViewBase sender, ContainerContentChangingEventArgs args) {
    // Phase 0: always register for Phase 1 so x:Bind DataTemplate bindings
    // get their initial evaluation. Returning early here (before registering)
    // would leave the container completely blank — no glyph, no label, nothing.
    if (args.Phase == 0) {
      args.RegisterUpdateCallback(OnContainerContentChanging);
      return;
    }

    if (args.InRecycleQueue) {
      args.ItemContainer.IsSelected = false;
      SetContainerSelectionBorder(args.ItemContainer, false);
      SetContainerDropTarget(args.ItemContainer, false);
      return;
    }

    // Phase 1+: data-model work now that x:Bind has initialised.
    var item = args.Item as ShellItem;

    if (item == null || string.IsNullOrEmpty(item.FullPath))
      return;

    var size = ThumbnailSizeForMode(ViewMode);
    var ext = item.IsFolder ? ":folder" : Path.GetExtension(item.FullPath).ToLowerInvariant();
    var ct = _thumbCts.Token;

    // Stamp a type-icon placeholder so the item is never blank while the
    // real thumbnail is still loading. Always refresh from cache on mode change
    // (guarded by HasRealThumbnail so real thumbnails are never downgraded).
    if (!item.HasRealThumbnail) {
      if (_typeIconCache.TryGetValue((ext, size), out var typeIcon))
        item.Icon = typeIcon;
      // else: leave Icon as-is (null or stale); worker will fetch the correct size
    }

    // Fix selection / drop-target state so recycled containers don't briefly
    // flash stale highlight from their previous item.
    args.ItemContainer.IsSelected = item.IsSelected;
    item.IsDropTarget = false;
    SetContainerSelectionBorder(args.ItemContainer, item.IsSelected);
    SetContainerDropTarget(args.ItemContainer, false);

    // Phase 1 — enqueue thumbnail upgrade if needed. Zero blocking work here.
    if (item.HasRealThumbnail)
      return;

    bool canUpgrade = !IsIconOnlyMode(ViewMode) &&
                      (item.IsFolder || _thumbnailExts.Contains(ext));
    bool isPerFile = _perFileIconExts.Contains(ext);

    if (canUpgrade || isPerFile || item.Icon == null)
      _thumbChannel.Writer.TryWrite((item, size, 0));
  }

  // ── Thumbnail worker ──────────────────────────────────────────────────────

  private void RestartThumbnailWorker() {
    _thumbCts.Cancel();
    _thumbCts = new CancellationTokenSource();
    _thumbChannel = CreateChannel();

    var reader = _thumbChannel.Reader;
    var ct = _thumbCts.Token;
    for (var i = 0; i < ThumbConcurrency; i++)
      _ = ProcessThumbnailQueueAsync(reader, ct);
  }

  private async Task ProcessThumbnailQueueAsync(
      ChannelReader<(ShellItem Item, uint Size, int Retry)> reader, CancellationToken ct) {
    // All pixel/COM work runs off the UI thread (ConfigureAwait(false)).
    // Only the final WriteableBitmap creation + item.Icon assignment is
    // dispatched back to the UI thread at Low priority, so it never preempts
    // Phase-1 callbacks or other layout work.
    var dq = DispatcherQueue;
    try {
      await foreach (var (item, size, retry) in reader.ReadAllAsync(ct).ConfigureAwait(false)) {
        if (ct.IsCancellationRequested)
          break;
        if (item.HasRealThumbnail)
          continue;

        var ext = item.IsFolder ? ":folder" : Path.GetExtension(item.FullPath).ToLowerInvariant();
        bool canUpgrade = item.IsFolder || _thumbnailExts.Contains(ext);
        bool isPerFile = _perFileIconExts.Contains(ext);

        try {
          if (!canUpgrade) {
            // ── Non-thumbnail items: just need a type/per-file icon ──────────
            byte[]? px = null;
            int w = 0, h = 0;
            if (isPerFile || !_typeIconCache.ContainsKey((ext, size))) {
              (px, w, h, _) = await NativeShell.GetShellImagePixelsAsync(
                  item.FullPath, size, NativeShell.SIIGBF.IconOnly, ct).ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested)
              continue;
            if (px != null) {
              var capPx = px;
              var capW = w;
              var capH = h;
              var capExt = ext;
              var capSize = size;
              dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
                if (ct.IsCancellationRequested)
                  return;
                var wb = NativeShell.PixelsToBitmapSync(capPx, capW, capH);
                if (wb == null)
                  return;
                if (!isPerFile)
                  _typeIconCache[(capExt, capSize)] = wb;
                if (!item.HasRealThumbnail)
                  item.Icon = wb;
              });
            } else if (_typeIconCache.TryGetValue((ext, size), out var cached)) {
              var capCached = cached;
              dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
                if (!item.HasRealThumbnail)
                  item.Icon = capCached;
              });
            }
            continue;
          }

          // ── Thumbnail-eligible items ─────────────────────────────────────
          // Fetch pixels entirely off-thread.
          byte[]? pixels;
          int pw, ph;
          bool isPending;

          if (NativeShell.IsCloudOnlyItem(item.FullPath)) {
            // Don't block this worker slot: fire an independent task governed by its
            // own high-concurrency semaphore so all 8 workers stay free for other items.
            _ = LoadCloudThumbnailAsync(item, size, retry, dq, ct);
            continue;
          } else {
            int hr;
            (pixels, pw, ph, hr) = await NativeShell.GetShellImagePixelsAsync(
                item.FullPath, size, NativeShell.SIIGBF.ResizeToFit, ct).ConfigureAwait(false);
            isPending = (hr == NativeShell.E_PENDING);
          }

          if (ct.IsCancellationRequested)
            continue;

          if (pixels != null) {
            var capPx = pixels;
            var capW = pw;
            var capH = ph;
            dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
              if (ct.IsCancellationRequested)
                return;
              var wb = NativeShell.PixelsToBitmapSync(capPx, capW, capH);
              if (wb != null) { item.Icon = wb; item.HasRealThumbnail = true; }
            });
          } else if (isPending || pixels == null) {
            if (retry < CloudThumbRetryMax) {
              int delayMs = Math.Min((retry + 1) * CloudThumbRetryBaseMs, CloudThumbRetryMaxMs);
              _ = Task.Delay(delayMs, ct).ContinueWith(_ => {
                if (!ct.IsCancellationRequested && !item.HasRealThumbnail)
                  _thumbChannel.Writer.TryWrite((item, size, retry + 1));
              }, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
          }
        } catch (OperationCanceledException) { break; } catch { }
      }
    } catch (OperationCanceledException) { }
  }

  // ── Cloud thumbnail loader ────────────────────────────────────────────────
  // Runs independently of the 8 main worker slots (governed by _storageApiSem)
  // so Storage-API latency never starves the workers processing other items.

  private async Task LoadCloudThumbnailAsync(
      ShellItem item, uint size, int retry, DispatcherQueue dq, CancellationToken ct) {
    // ── 1. App-level cache hit — instant, no API calls needed ────────────────
    if (_thumbCache.TryGetValue((item.FullPath, size), out var hit)) {
      var capHit = hit;
      dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
        if (ct.IsCancellationRequested || item.HasRealThumbnail) return;
        item.Icon = capHit;
        item.HasRealThumbnail = true;
      });
      return;
    }

    // ── 2. Acquire one of the high-concurrency Storage-API slots ─────────────
    try { await _storageApiSem.WaitAsync(ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return; }

    try {
      if (ct.IsCancellationRequested || item.HasRealThumbnail) return;

      // ── 3. Fast shell-cache probe (no broker call, no file download) ───────
      var (shellPx, shellW, shellH, _) = await NativeShell.GetShellImagePixelsAsync(
          item.FullPath, size, NativeShell.SIIGBF.CacheOnly_Thumb, ct).ConfigureAwait(false);

      byte[]? finalPx = null;
      int finalW = 0, finalH = 0;

      if (shellPx != null && !ct.IsCancellationRequested) {
        if (shellW >= (int)size || shellH >= (int)size) {
          // Shell cache already has a full-size result — skip Storage API entirely.
          finalPx = shellPx; finalW = shellW; finalH = shellH;
        } else {
          // Shell has a small placeholder — show it immediately as a low-res preview
          // so the item is never blank while the full-size version loads below.
          var capSmallPx = shellPx; var capSmallW = shellW; var capSmallH = shellH;
          dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (ct.IsCancellationRequested || item.HasRealThumbnail) return;
            var wb = NativeShell.PixelsToBitmapSync(capSmallPx, capSmallW, capSmallH);
            if (wb != null && !item.HasRealThumbnail) item.Icon = wb;
          });
        }
      }

      // ── 4. Full-resolution Storage API fetch (only when shell cache missed) ─
      if (finalPx == null && !ct.IsCancellationRequested) {
        (finalPx, finalW, finalH) = await NativeShell.GetStorageThumbnailPixelsAsync(
            item.FullPath, size, ct).ConfigureAwait(false);
      }

      if (ct.IsCancellationRequested) return;

      if (finalPx != null) {
        // ── 5. Materialise on the UI thread, then populate the cache ──────────
        var capPx = finalPx; var capW = finalW; var capH = finalH;
        var capPath = item.FullPath;
        dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
          if (ct.IsCancellationRequested || item.HasRealThumbnail) return;
          var wb = NativeShell.PixelsToBitmapSync(capPx, capW, capH);
          if (wb == null) return;
          // Trim cache to cap before inserting (simple FIFO eviction).
          if (_thumbCache.Count >= ThumbCacheMaxSize) {
            foreach (var k in _thumbCache.Keys.Take(1))
              _thumbCache.TryRemove(k, out _);
          }
          _thumbCache[(capPath, size)] = wb;
          item.Icon = wb;
          item.HasRealThumbnail = true;
        });
      } else {
        // ── 6. Nothing available yet — schedule a retry with back-off ─────────
        if (retry < CloudThumbRetryMax) {
          int delayMs = Math.Min((retry + 1) * CloudThumbRetryBaseMs, CloudThumbRetryMaxMs);
          _ = Task.Delay(delayMs, ct).ContinueWith(_ => {
            if (!ct.IsCancellationRequested && !item.HasRealThumbnail)
              _thumbChannel.Writer.TryWrite((item, size, retry + 1));
          }, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
      }
    } catch (OperationCanceledException) {
    } catch { }
    finally { _storageApiSem.Release(); }
  }

  // ── Double-tap navigation ─────────────────────────────────────────────────

  private void ShellView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) {
    var dep = e.OriginalSource as DependencyObject;
    while (dep is not null and not ListViewItem)
      dep = VisualTreeHelper.GetParent(dep);

    if (dep is not ListViewItem { Content: ShellItem item })
      return;

    if (item.IsFolder) {
      Navigate(item.FullPath);
    } else {
      try {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FullPath) {
          UseShellExecute = true
        });
      } catch { }
    }
  }

  // ── Rubber-band drag selection ────────────────────────────────────────────

  private bool _isDraggingSelection;
  private Point _dragStartInContent;
  private uint _dragPointerId;
  private HashSet<ShellItem> _preDragSelection = [];
  private HashSet<ShellItem> _dragSelectedItems = [];
  private HashSet<ShellItem> _hitTestScratch = [];
  private Dictionary<ShellItem, int> _itemIndexMap = [];
  private Point _lastHitTestPoint = new(-1, -1);
  private ScrollViewer? _scrollViewer;
  private Point _lastPointerInShellView;
  private Point _lastDragPointerCanvas;
  private bool _autoScrollSubscribed;
  private TimeSpan _lastRenderTime;
  private const double AutoScrollZone = 60;
  private const double AutoScrollPixelsPerSecond = 500;
  private bool _suppressSelectionChanged;

  // Pending item-drag tracking (manual drag initiation)
  private ListViewItem? _pendingDragContainer;
  private Point _pendingDragStart;
  private const double DragThreshold = 4.0;

  private readonly List<int> _toSelect = [];
  private readonly List<int> _toDeselect = [];

  /// <summary>
  /// Sorts <paramref name="indices"/>, then collapses them into contiguous ranges and calls
  /// <paramref name="apply"/> once per range.  This replaces N individual SelectRange /
  /// DeselectRange calls (each firing SelectionChanged) with at most a handful of calls.
  /// </summary>
  private static void ApplyIndexRanges(
      Action<Microsoft.UI.Xaml.Data.ItemIndexRange> apply, List<int> indices) {
    if (indices.Count == 0)
      return;
    indices.Sort();
    int start = indices[0], last = indices[0];
    for (int i = 1; i < indices.Count; i++) {
      if (indices[i] == last + 1) { last = indices[i]; continue; }
      apply(new Microsoft.UI.Xaml.Data.ItemIndexRange(start, (uint)(last - start + 1)));
      start = last = indices[i];
    }
    apply(new Microsoft.UI.Xaml.Data.ItemIndexRange(start, (uint)(last - start + 1)));
  }

  private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject {
    int count = VisualTreeHelper.GetChildrenCount(parent);
    for (int i = 0; i < count; i++) {
      var child = VisualTreeHelper.GetChild(parent, i);
      if (child is T match)
        return match;
      var result = FindDescendant<T>(child);
      if (result is not null)
        return result;
    }
    return null;
  }

  private static bool IsCtrlDown() =>
      (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
          Windows.System.VirtualKey.Control) &
       Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

  private static bool RectsIntersect(Rect a, Rect b) =>
      a.Left < b.Right && a.Right > b.Left &&
      a.Top < b.Bottom && a.Bottom > b.Top;

  private static bool ClickIsOnItemContent(DependencyObject? hitDep) {
    var dep = hitDep;
    while (dep is not null and not ListViewItem and not ListView)
      dep = VisualTreeHelper.GetParent(dep);
    return dep is ListViewItem;
  }

  private static bool ClickIsOnScrollBar(DependencyObject? hitDep) {
    var dep = hitDep;
    while (dep is not null) {
      if (dep is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar)
        return true;
      dep = VisualTreeHelper.GetParent(dep);
    }
    return false;
  }

  private void OnDragSelectPointerPressed(object sender, PointerRoutedEventArgs e) {
    var pt = e.GetCurrentPoint(DragSelectGrid);
    if (!pt.Properties.IsLeftButtonPressed)
      return;

    if (_isDraggingSelection)
      EndDragSelect(null);

    if (ClickIsOnScrollBar(e.OriginalSource as DependencyObject))
      return;

    // If the press landed on an item, remember the container for a potential drag —
    // but do NOT start rubber-band selection.
    if (ClickIsOnItemContent(e.OriginalSource as DependencyObject)) {
      var dep = e.OriginalSource as DependencyObject;
      while (dep is not null and not ListViewItem and not ListView)
        dep = VisualTreeHelper.GetParent(dep);
      if (dep is ListViewItem lvi) {
        _pendingDragContainer = lvi;
        _pendingDragStart = e.GetCurrentPoint(DragSelectGrid).Position;
      }
      return;
    }

    _pendingDragContainer = null;

    _itemIndexMap.Clear();
    for (int i = 0; i < Items.Count; i++)
      _itemIndexMap[Items[i]] = i;

    if (!IsCtrlDown())
      ShellView.DeselectRange(
          new Microsoft.UI.Xaml.Data.ItemIndexRange(0, (uint)Items.Count));

    _preDragSelection = [.. ShellView.SelectedItems.Cast<ShellItem>()];
    _dragSelectedItems.Clear();
    _lastHitTestPoint = new Point(-1, -1);

    var posInCanvas = e.GetCurrentPoint(SelectionCanvas).Position;
    double scrollOffset = _scrollViewer?.VerticalOffset ?? 0;
    _dragStartInContent = new Point(posInCanvas.X, posInCanvas.Y + scrollOffset);
    _dragPointerId = pt.PointerId;
    _isDraggingSelection = true;
    _lastDragPointerCanvas = posInCanvas;
    _lastPointerInShellView = e.GetCurrentPoint(ShellView).Position;

    UpdateSelectionRect(posInCanvas);
    SelectionRect.Visibility = Visibility.Visible;
    DragSelectGrid.CapturePointer(e.Pointer);
    e.Handled = true;
  }

  private void OnDragSelectPointerMoved(object sender, PointerRoutedEventArgs e) {
    // Handle pending item drag (threshold detection).
    if (_pendingDragContainer is not null) {
      var pos = e.GetCurrentPoint(DragSelectGrid).Position;
      double dx = pos.X - _pendingDragStart.X;
      double dy = pos.Y - _pendingDragStart.Y;
      if (dx * dx + dy * dy >= DragThreshold * DragThreshold) {
        var container = _pendingDragContainer;
        _pendingDragContainer = null;
        _ = BeginItemDragAsync(container, e);
      }
      return;
    }

    if (!_isDraggingSelection || e.Pointer.PointerId != _dragPointerId)
      return;

    _lastDragPointerCanvas = e.GetCurrentPoint(SelectionCanvas).Position;
    _lastPointerInShellView = e.GetCurrentPoint(ShellView).Position;

    UpdateSelectionRect(_lastDragPointerCanvas);
    HitTestItems();
    UpdateAutoScroll(_lastPointerInShellView);
    e.Handled = true;
  }

  private void OnDragSelectPointerReleased(object sender, PointerRoutedEventArgs e) {
    _pendingDragContainer = null;
    if (!_isDraggingSelection || e.Pointer.PointerId != _dragPointerId)
      return;
    EndDragSelect(e.Pointer);
    e.Handled = true;
  }

  private void OnDragSelectPointerCaptureLost(object sender, PointerRoutedEventArgs e) {
    _pendingDragContainer = null;
    if (_isDraggingSelection && e.Pointer.PointerId == _dragPointerId)
      EndDragSelect(null);
  }

  private void EndDragSelect(Pointer? pointer) {
    _isDraggingSelection = false;
    StopAutoScroll();
    SelectionRect.Visibility = Visibility.Collapsed;
    SelectionRect.Width = 0;
    SelectionRect.Height = 0;
    if (pointer is not null)
      DragSelectGrid.ReleasePointerCapture(pointer);
  }

  private void UpdateSelectionRect(Point currentInCanvas) {
    double scrollOffset = _scrollViewer?.VerticalOffset ?? 0;
    double startX = _dragStartInContent.X;
    double startY = _dragStartInContent.Y - scrollOffset;

    double x = Math.Min(startX, currentInCanvas.X);
    double y = Math.Min(startY, currentInCanvas.Y);
    double w = Math.Abs(currentInCanvas.X - startX);
    double h = Math.Abs(currentInCanvas.Y - startY);

    double canvasW = SelectionCanvas.ActualWidth;
    double canvasH = SelectionCanvas.ActualHeight;
    double right = Math.Min(x + w, canvasW);
    double bottom = Math.Min(y + h, canvasH);
    x = Math.Max(x, 0);
    y = Math.Max(y, 0);
    w = Math.Max(right - x, 0);
    h = Math.Max(bottom - y, 0);

    Canvas.SetLeft(SelectionRect, x);
    Canvas.SetTop(SelectionRect, y);
    SelectionRect.Width = w;
    SelectionRect.Height = h;
  }

  private void HitTestItems() {
    if (ShellView.ItemsPanelRoot is not Panel panel)
      return;

    double dx = _lastDragPointerCanvas.X - _lastHitTestPoint.X;
    double dy = _lastDragPointerCanvas.Y - _lastHitTestPoint.Y;
    if (dx * dx + dy * dy < 64)
      return;   // 8 px dead zone — skip trivial moves
    _lastHitTestPoint = _lastDragPointerCanvas;

    double scrollOffset = _scrollViewer?.VerticalOffset ?? 0;
    double startX = _dragStartInContent.X;
    double startY = _dragStartInContent.Y - scrollOffset;

    double selX = Math.Min(startX, _lastDragPointerCanvas.X);
    double selY = Math.Min(startY, _lastDragPointerCanvas.Y);
    double selW = Math.Abs(_lastDragPointerCanvas.X - startX);
    double selH = Math.Abs(_lastDragPointerCanvas.Y - startY);
    var selRect = new Rect(selX, selY, selW, selH);

    var panelOrigin = panel.TransformToVisual(SelectionCanvas).TransformPoint(new Point(0, 0));

    _hitTestScratch.Clear();
    foreach (var child in panel.Children) {
      if (child is not ListViewItem container)
        continue;
      if (container.Content is not ShellItem item)
        continue;
      if (container.ActualWidth == 0 || container.ActualHeight == 0)
        continue;

      var off = container.ActualOffset;
      var itemRect = new Rect(
          panelOrigin.X + off.X, panelOrigin.Y + off.Y,
          container.ActualWidth, container.ActualHeight);

      if (RectsIntersect(selRect, itemRect))
        _hitTestScratch.Add(item);
    }

    // ── Compute deltas ────────────────────────────────────────────────────
    _toDeselect.Clear();
    foreach (var item in _dragSelectedItems)
      if (!_hitTestScratch.Contains(item) && !_preDragSelection.Contains(item))
        if (_itemIndexMap.TryGetValue(item, out int idx))
          _toDeselect.Add(idx);

    _toSelect.Clear();
    foreach (var item in _hitTestScratch)
      if (!_dragSelectedItems.Contains(item))
        if (_itemIndexMap.TryGetValue(item, out int idx))
          _toSelect.Add(idx);

    // ── Apply as batched contiguous ranges ────────────────────────────────
    // Suppress OnShellViewSelectionChanged so we don't pay the per-item
    // IsSelected setter cost from the event — we sync the model ourselves below.
    _suppressSelectionChanged = true;
    try {
      ApplyIndexRanges(ShellView.DeselectRange, _toDeselect);
      ApplyIndexRanges(ShellView.SelectRange, _toSelect);
    } finally {
      _suppressSelectionChanged = false;
    }

    // ── Sync IsSelected on the data model for changed items only ──────────
    foreach (var item in _dragSelectedItems)
      if (!_hitTestScratch.Contains(item) && !_preDragSelection.Contains(item)) {
        item.IsSelected = false;
        if (ShellView.ContainerFromItem(item) is ListViewItem c)
          SetContainerSelectionBorder(c, false);
      }
    foreach (var item in _hitTestScratch)
      if (!_dragSelectedItems.Contains(item)) {
        item.IsSelected = true;
        if (ShellView.ContainerFromItem(item) is ListViewItem c)
          SetContainerSelectionBorder(c, true);
      }

    (_dragSelectedItems, _hitTestScratch) = (_hitTestScratch, _dragSelectedItems);
  }

  private void UpdateAutoScroll(Point pointerInShellView) {
    bool nearEdge = pointerInShellView.Y < AutoScrollZone ||
                    pointerInShellView.Y > ShellView.ActualHeight - AutoScrollZone;
    if (nearEdge) {
      if (!_autoScrollSubscribed) {
        _lastRenderTime = TimeSpan.Zero;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnAutoScrollRendering;
        _autoScrollSubscribed = true;
      }
    } else {
      StopAutoScroll();
    }
  }

  private void StopAutoScroll() {
    if (!_autoScrollSubscribed)
      return;
    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnAutoScrollRendering;
    _autoScrollSubscribed = false;
  }

  private void OnAutoScrollRendering(object? sender, object e) {
    if (!_isDraggingSelection || _scrollViewer is null) { StopAutoScroll(); return; }

    // Delta-time from rendering time — consistent velocity regardless of actual frame rate.
    var renderTime = (e as Microsoft.UI.Xaml.Media.RenderingEventArgs)?.RenderingTime
                     ?? TimeSpan.Zero;
    double dt = _lastRenderTime == TimeSpan.Zero
        ? 0
        : Math.Min((renderTime - _lastRenderTime).TotalSeconds, 0.05); // cap at 50 ms
    _lastRenderTime = renderTime;

    double viewHeight = ShellView.ActualHeight;
    double scrollDelta = 0;

    if (_lastPointerInShellView.Y < AutoScrollZone) {
      double factor = 1.0 - (_lastPointerInShellView.Y / AutoScrollZone);
      scrollDelta = -AutoScrollPixelsPerSecond * factor * dt;
    } else if (_lastPointerInShellView.Y > viewHeight - AutoScrollZone) {
      double factor = (_lastPointerInShellView.Y - (viewHeight - AutoScrollZone)) / AutoScrollZone;
      scrollDelta = AutoScrollPixelsPerSecond * factor * dt;
    }

    if (scrollDelta == 0 || dt == 0)
      return;

    double newOffset = Math.Clamp(
        _scrollViewer.VerticalOffset + scrollDelta, 0, _scrollViewer.ScrollableHeight);
    if (Math.Abs(newOffset - _scrollViewer.VerticalOffset) < 0.5)
      return;

    _scrollViewer.ChangeView(null, newOffset, null, true);
    UpdateSelectionRect(_lastDragPointerCanvas);
    _lastHitTestPoint = new Point(-1, -1);
    HitTestItems();
  }

  // ── View mode ─────────────────────────────────────────────────────────────

  private static uint ThumbnailSizeForMode(ShellViewMode mode) => mode switch {
    ShellViewMode.ExtraLargeIcons => 256,
    ShellViewMode.LargeIcons => 128,
    ShellViewMode.MediumIcons => 96,
    ShellViewMode.SmallIcons or ShellViewMode.Tiles
        or ShellViewMode.Content => 48,
    _ => 16
  };

  private static bool IsIconOnlyMode(ShellViewMode mode) =>
      mode is ShellViewMode.Details or ShellViewMode.List;

  private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
    if (d is ShellListView ctrl) {
      ctrl.ApplyViewMode((ShellViewMode)e.NewValue);
      if (!ctrl._applyingFolderSettings)
        ctrl.SaveCurrentFolderSettings();
    }
  }

  private void ApplyViewMode(ShellViewMode mode) {
    CollapseAllNameExpansions();
    RestartThumbnailWorker();
    var size = ThumbnailSizeForMode(mode);
    foreach (var item in Items) {
      item.HasRealThumbnail = false;
      var ext = item.IsFolder ? ":folder" : Path.GetExtension(item.FullPath).ToLowerInvariant();
      if (_typeIconCache.TryGetValue((ext, size), out var icon))
        item.Icon = icon;
      else
        item.Icon = null;
    }

    string templateKey = mode switch {
      ShellViewMode.ExtraLargeIcons => "ExtraLargeIconsTemplate",
      ShellViewMode.LargeIcons => "LargeIconsTemplate",
      ShellViewMode.MediumIcons => "MediumIconsTemplate",
      ShellViewMode.SmallIcons => "SmallIconsTemplate",
      ShellViewMode.List => "ListTemplate",
      ShellViewMode.Details => "DetailsTemplate",
      ShellViewMode.Tiles => "TilesTemplate",
      ShellViewMode.Content => "ContentTemplate",
      _ => "SmallIconsTemplate"
    };

    ShellView.ItemTemplate = (DataTemplate)Resources[templateKey];

    bool isWrapMode = mode is ShellViewMode.ExtraLargeIcons
        or ShellViewMode.LargeIcons or ShellViewMode.MediumIcons
        or ShellViewMode.SmallIcons or ShellViewMode.Tiles;

    ShellView.ItemContainerStyle = isWrapMode
        ? (Style)Resources["WrapIconListViewItemStyle"]
        : new Style(typeof(ListViewItem)) {
          BasedOn = (Style)Application.Current.Resources["DefaultListViewItemStyle"],
          Setters =
            {
                    new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Left),
                    new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left),
                    new Setter(Control.PaddingProperty, new Thickness(0)),
                    new Setter(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1)),
                    new Setter(Control.UseSystemFocusVisualsProperty, false),
                    new Setter(FrameworkElement.FocusVisualMarginProperty, new Thickness(0)),
                    new Setter(Control.FocusVisualPrimaryThicknessProperty, new Thickness(0)),
                    new Setter(Control.FocusVisualSecondaryThicknessProperty, new Thickness(0))
            }
        };

    ShellView.ItemsPanel = mode switch {
      ShellViewMode.List or ShellViewMode.Details or ShellViewMode.Content
          => BuildStackingPanel(),
      ShellViewMode.ExtraLargeIcons => BuildWrapPanel(256 + 8 + 57),
      ShellViewMode.LargeIcons => BuildWrapPanel(128 + 8 + 57),
      ShellViewMode.MediumIcons => BuildWrapPanel(96 + 8 + 57),
      ShellViewMode.SmallIcons => BuildWrapPanel(48 + 8 + 57),
      _ => BuildWrapPanel()
    };

    DetailsHeader.Visibility = Visibility.Visible;
    _currentMode = mode;
    DetailsHeaderRepeater.IsHitTestVisible = true;

    // For non-Details views every column gets a fixed 200 px width.
    // For Details view, restore the widths that were saved for the current folder.
    if (mode != ShellViewMode.Details) {
      foreach (var col in DetailsColumns.Columns)
        col.Width = 200;
    } else if (!string.IsNullOrEmpty(CurrentPath)) {
      // Best-effort async restore; runs without blocking the UI.
      _ = RestoreDetailsColumnWidthsAsync(CurrentPath);
    }
  }

  private async Task RestoreDetailsColumnWidthsAsync(string path) {
    var settings = await FolderSettingsDb.Instance.LoadAsync(path);
    foreach (var rec in settings.Columns) {
      var col = DetailsColumns.Columns.FirstOrDefault(c => c.Key == rec.Key);
      if (col is not null)
        col.Width = rec.Width;
    }
  }

  private static ItemsPanelTemplate BuildWrapPanel(double itemHeight = 0) {
    string heightAttr = itemHeight > 0 ? $" ItemHeight=\"{itemHeight}\"" : string.Empty;
    return (ItemsPanelTemplate)XamlLoad(
        $"""
            <ItemsPanelTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsWrapGrid Orientation="Horizontal"{heightAttr} />
            </ItemsPanelTemplate>
            """);
  }

  private static ItemsPanelTemplate BuildStackingPanel() =>
      (ItemsPanelTemplate)XamlLoad(
          """
            <ItemsPanelTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsStackPanel Orientation="Vertical" />
            </ItemsPanelTemplate>
            """);

  private static object XamlLoad(string xaml) =>
      Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);

  // ── Viewport estimation ───────────────────────────────────────────────────

  private int EstimateViewportItemCount(ShellViewMode mode) {
    double vw = ShellView.ActualWidth;
    double vh = ShellView.ActualHeight;
    if (vw <= 0 || vh <= 0)
      return 80;

    return mode switch {
      ShellViewMode.ExtraLargeIcons => WrapCount(vw, vh, 284, 321),
      ShellViewMode.LargeIcons => WrapCount(vw, vh, 156, 193),
      ShellViewMode.MediumIcons => WrapCount(vw, vh, 124, 161),
      ShellViewMode.SmallIcons => WrapCount(vw, vh, 88, 113),
      ShellViewMode.Tiles => WrapCount(vw, vh, 268, 56),
      ShellViewMode.List => StackCount(vh, 24),
      ShellViewMode.Details => StackCount(vh, 28),
      ShellViewMode.Content => StackCount(vh, 60),
      _ => 80
    };
  }

  private static int WrapCount(double vw, double vh, double itemW, double itemH) {
    int cols = Math.Max(1, (int)(vw / itemW));
    int rows = (int)Math.Ceiling(vh / itemH) + 1;
    return cols * rows;
  }

  private static int StackCount(double vh, double rowH) =>
      (int)Math.Ceiling(vh / rowH) + 4;

  // ── Details column resize ─────────────────────────────────────────────────

  private void OnDetailsHeaderElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs e) {
    if (e.Element is not Grid cell) return;

    // Wire column drag-reorder on the cell Grid (Details mode only).
    cell.PointerPressed  += OnColDragPointerPressed;
    cell.PointerMoved    += OnColDragPointerMoved;
    cell.PointerReleased += OnColDragPointerReleased;
    cell.PointerCaptureLost += OnColDragCaptureLost;

    // Find the gripper Border (child 1) and wire resize events.
    if (VisualTreeHelper.GetChildrenCount(cell) >= 2 &&
        VisualTreeHelper.GetChild(cell, 1) is Border gripper) {
      gripper.PointerEntered     += OnGripperPointerEntered;
      gripper.PointerExited      += OnGripperPointerExited;
      gripper.PointerPressed     += OnColumnGripperPressed;
      gripper.PointerMoved       += OnColumnGripperMoved;
      gripper.PointerReleased    += OnColumnGripperReleased;
      gripper.PointerCaptureLost += OnColumnGripperCaptureLost;
    }
  }

  // ── Column drag-reorder ───────────────────────────────────────────────────

  private void OnColDragPointerPressed(object sender, PointerRoutedEventArgs e) {
    if (_currentMode != ShellViewMode.Details) return;
    if (sender is not Grid cell) return;
    var key = (string)cell.Tag;
    var idx = DetailsColumns.Columns.ToList().FindIndex(c => c.Key == key);
    if (idx < 0) return;

    _colDragSourceIndex   = idx;
    _colDragging          = false;
    _colDragStartX        = e.GetCurrentPoint(DetailsHeader).Position.X;
    _colDragCell          = cell;
    _colDragPopupOffsetX  = e.GetCurrentPoint(cell).Position.X;
    cell.CapturePointer(e.Pointer);
    // Do NOT set e.Handled — Tapped must still fire for sort on a plain click.
  }

  private void OnColDragPointerMoved(object sender, PointerRoutedEventArgs e) {
    if (_colDragSourceIndex < 0 || sender != _colDragCell) return;
    var x = e.GetCurrentPoint(DetailsHeader).Position.X;
    if (!_colDragging) {
      if (Math.Abs(x - _colDragStartX) < 6) return; // threshold
      _colDragging = true;
      ColumnDropIndicator.Visibility = Visibility.Visible;
      // Set ghost label from the dragged column's header text.
      ColDragGhostText.Text = DetailsColumns.Columns[_colDragSourceIndex].Header;
      ColDragGhost.MinWidth  = DetailsColumns.Columns[_colDragSourceIndex].Width;
      ColDragGhost.Visibility = Visibility.Visible;
    }
    // Move the ghost so the grab point stays under the cursor.
    var pos = e.GetCurrentPoint(RootGrid).Position;
    Canvas.SetLeft(ColDragGhost, pos.X - _colDragPopupOffsetX);
    Canvas.SetTop(ColDragGhost, pos.Y - ColDragGhost.ActualHeight / 2);
    // Snap indicator to the nearest column boundary.
    var snapX = SnapIndicatorX(x) - DetailsHeader.Padding.Left - 1;
    ColumnDropIndicator.Margin = new Thickness(Math.Max(0, snapX), 0, 0, 0);
    e.Handled = true;
  }

  private void OnColDragPointerReleased(object sender, PointerRoutedEventArgs e) {
    // Snapshot state BEFORE releasing captures — ReleasePointerCaptures fires
    // PointerCaptureLost synchronously, which calls ResetColDrag and clears
    // _colDragging/_colDragSourceIndex before we can read them.
    var wasDragging = _colDragging;
    var fromIdx     = _colDragSourceIndex;
    var x           = e.GetCurrentPoint(DetailsHeader).Position.X;

    if (sender is Grid cell) cell.ReleasePointerCaptures();
    ResetColDrag();

    if (!wasDragging || fromIdx < 0) return;

    var targetIdx = DropIndexFromX(x);
    var to = targetIdx > fromIdx ? targetIdx - 1 : targetIdx;
    if (to != fromIdx && to >= 0 && to < DetailsColumns.Columns.Count)
      DetailsColumns.Columns.Move(fromIdx, to);

    e.Handled = true;
  }

  private void OnColDragCaptureLost(object sender, PointerRoutedEventArgs e) => ResetColDrag();

  private void ResetColDrag() {
    _colDragging        = false;
    _colDragCell        = null;
    _colDragSourceIndex = -1;
    ColumnDropIndicator.Visibility = Visibility.Collapsed;
    ColDragGhost.Visibility        = Visibility.Collapsed;
  }

  /// Returns the X position (relative to DetailsHeader) to snap the indicator to —
  /// whichever column boundary is closest to <paramref name="x"/>.
  private double SnapIndicatorX(double x) {
    double best  = double.MaxValue;
    double bestX = 0;
    double cursor = DetailsHeader.Padding.Left;
    for (int i = 0; i <= DetailsColumns.Columns.Count; i++) {
      double dist = Math.Abs(cursor - x);
      if (dist < best) { best = dist; bestX = cursor; }
      if (i < DetailsColumns.Columns.Count)
        cursor += DetailsColumns.Columns[i].Width;
    }
    return bestX;
  }

  /// Returns the column index (0-based, can equal Count for "after last") where
  /// a drop at <paramref name="x"/> should insert the dragged column.
  private int DropIndexFromX(double x) {
    double cursor = DetailsHeader.Padding.Left;
    for (int i = 0; i < DetailsColumns.Columns.Count; i++) {
      double mid = cursor + DetailsColumns.Columns[i].Width / 2;
      if (x < mid) return i;
      cursor += DetailsColumns.Columns[i].Width;
    }
    return DetailsColumns.Columns.Count;
  }

  private void OnGripperPointerEntered(object sender, PointerRoutedEventArgs e) {
    if (_currentMode == ShellViewMode.Details)
      ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
  }

  private void OnGripperPointerExited(object sender, PointerRoutedEventArgs e) {
    if (_activeGripper is null)
      ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
  }

  private void OnColumnGripperPressed(object sender, PointerRoutedEventArgs e) {
    if (sender is not Border gripper || _currentMode != ShellViewMode.Details)
      return;
    _activeGripper = gripper;
    _gripperStartX = e.GetCurrentPoint(DetailsHeader).Position.X;
    var key = (string)gripper.Tag;
    _gripperStartWidth = DetailsColumns[key].Width;
    gripper.CapturePointer(e.Pointer);
    // Do NOT mark e.Handled here — that would suppress Tapped on the parent Grid.
  }

  private void OnColumnGripperMoved(object sender, PointerRoutedEventArgs e) {
    if (_activeGripper is null || sender != _activeGripper)
      return;
    double delta = e.GetCurrentPoint(DetailsHeader).Position.X - _gripperStartX;
    double newWidth = Math.Max(40, _gripperStartWidth + delta);
    DetailsColumns[(string)_activeGripper.Tag].Width = newWidth;
    e.Handled = true;
  }

  private void OnColumnGripperReleased(object sender, PointerRoutedEventArgs e) {
    if (sender is Border gripper && gripper == _activeGripper) {
      gripper.ReleasePointerCapture(e.Pointer);
      _activeGripper = null;
      ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
      SaveCurrentFolderSettings();
    }
    e.Handled = true;
  }

  private void OnColumnGripperCaptureLost(object sender, PointerRoutedEventArgs e) {
    _activeGripper = null;
    ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
  }

  // ── Column sort ───────────────────────────────────────────────────────────

  private void OnColumnHeaderTapped(object sender, TappedRoutedEventArgs e) {
    if (sender is not FrameworkElement el)
      return;
    var col = (string)el.Tag;
    if (_sortColumn == col)
      _sortAscending = !_sortAscending;
    else { _sortColumn = col; _sortAscending = true; }
    ApplySortToCollection();
    UpdateSortIndicators();
    SaveCurrentFolderSettings();
    SortChanged?.Invoke(this, EventArgs.Empty);
  }

  private void ApplySortToCollection() {
    var sorted = SortItems(Items.ToList());
    Items.Reset(sorted);
  }

  /// <summary>
  /// Sets the sort column and direction, re-sorts the visible items, updates
  /// the column-header indicators, persists the change, and fires <see cref="SortChanged"/>.
  /// </summary>
  public void ApplySort(string column, bool ascending) {
    _sortColumn    = column;
    _sortAscending = ascending;
    ApplySortToCollection();
    UpdateSortIndicators();
    SaveCurrentFolderSettings();
    SortChanged?.Invoke(this, EventArgs.Empty);
  }

  internal List<ShellItem> SortItems(List<ShellItem> list) {
    Func<ShellItem, object?> keySelector = _sortColumn switch {
      "Date" => it => it.DateModified,
      "Type" => it => it.ItemType,
      "Size" => it => it.SizeBytes,
      _ => it => it.Name,
    };

    IOrderedEnumerable<ShellItem> ordered;
    if (_sortAscending)
      ordered = list.OrderBy(it => !it.IsFolder).ThenBy(keySelector);
    else
      ordered = list.OrderBy(it => !it.IsFolder).ThenByDescending(keySelector);

    return ordered.ToList();
  }

  private void UpdateSortIndicators() {
    foreach (var col in DetailsColumns.Columns)
      col.SortIndicator = _sortColumn == col.Key
          ? (_sortAscending ? " ▲" : " ▼")
          : string.Empty;
  }

  // ── Folder settings persistence ───────────────────────────────────────────

  /// <summary>
  /// Loads stored settings for <paramref name="path"/> from the SQLite store and
  /// applies them to the current sort state, column widths/order, and view mode.
  /// Must be called before Items is populated so SortItems picks up the right key.
  /// </summary>
  private async Task ApplyFolderSettings(string path) {
    var settings = await FolderSettingsDb.Instance.LoadAsync(path);

    _applyingFolderSettings = true;
    try {
      // Apply sort state.
      _sortColumn   = settings.SortColumn;
      _sortAscending = settings.SortAscending;

      // Apply column order and widths.
      var dc = DetailsColumns;
      var newColumns = new System.Collections.Generic.List<BetterExplorer.ShellApi.DetailsColumn>();
      foreach (var rec in settings.Columns) {
        var existing = dc.Columns.FirstOrDefault(c => c.Key == rec.Key);
        if (existing is not null) {
          // Only restore saved widths in Details view; all other views use a fixed 200 px.
          existing.Width = settings.ViewMode == ShellViewMode.Details ? rec.Width : 200;
          newColumns.Add(existing);
        }
      }
      // Any columns that are in the current set but not in the saved settings go at the end.
      foreach (var col in dc.Columns)
        if (!newColumns.Contains(col)) {
          col.Width = settings.ViewMode == ShellViewMode.Details ? col.Width : 200;
          newColumns.Add(col);
        }

      dc.Columns.Clear();
      foreach (var col in newColumns)
        dc.Columns.Add(col);

      // Apply view mode (will fire OnViewModeChanged → ApplyViewMode).
      ViewMode = settings.ViewMode;
    } finally {
      _applyingFolderSettings = false;
    }

    UpdateSortIndicators();
  }

  /// <summary>
  /// Captures the current sort state, column order/widths, and view mode and
  /// persists them for <see cref="CurrentPath"/> in the SQLite store.
  /// No-ops when <see cref="CurrentPath"/> is empty.
  /// </summary>
  private void SaveCurrentFolderSettings() {
    if (string.IsNullOrEmpty(CurrentPath) || _applyingFolderSettings)
      return;

    var columnRecords = DetailsColumns.Columns
        .Select(c => new ColumnRecord(c.Key, c.Width))
        .ToList();

    var settings = new FolderSettings {
      Columns       = columnRecords,
      SortColumn    = _sortColumn,
      SortAscending = _sortAscending,
      ViewMode      = ViewMode,
    };

    FolderSettingsDb.Instance.Save(CurrentPath, settings);
  }

  // ── Drag and Drop ─────────────────────────────────────────────────────────

  private ShellItem? _currentDragTarget;
  private ListViewItem? _currentDragContainer;
  private bool _internalDropHandled;
  private int _dragItemCount;

  private void ClearDragTarget() {
    if (_currentDragContainer is not null) {
      VisualStateManager.GoToState(_currentDragContainer, "Normal", true);
      SetContainerDropTarget(_currentDragContainer, false);
      _currentDragContainer = null;
    }
    if (_currentDragTarget is null)
      return;
    _currentDragTarget.IsDropTarget = false;
    _currentDragTarget = null;
  }

  /// <summary>
  /// Composites up to 3 item icons into a stacked shell-style drag image.
  /// Each successive layer is drawn offset by <paramref name="layerOffset"/> pixels
  /// toward the lower-right and blended at decreasing opacity.
  /// Returns <see langword="null"/> if no valid icon bitmaps are available.
  /// </summary>
  private static SoftwareBitmap? BuildDragSoftwareBitmap(
      IReadOnlyList<ShellItem> items, int iconSize = 64, int layerOffset = 10) {
    // Collect up to 3 WriteableBitmaps (back→front order so index 0 is drawn last on top).
    var icons = items
        .Take(3)
        .Select(i => i.Icon as WriteableBitmap)
        .Where(b => b is not null)
        .Cast<WriteableBitmap>()
        .ToList();

    if (icons.Count == 0)
      return null;

    int layers = icons.Count;
    int totalSize = iconSize + layerOffset * (layers - 1);
    int stride = totalSize * 4;
    var dst = new byte[totalSize * stride]; // premultiplied BGRA

    // Draw back-layers first (index layers-1 … 1) then front (index 0).
    // Layer opacities: front=1.0, mid=0.65, back=0.40
    float[] opacities = [1.0f, 0.65f, 0.40f];

    for (int li = layers - 1; li >= 0; li--) {
      var wb = icons[li];
      float op = opacities[Math.Min(li, opacities.Length - 1)];

      // Pixel offset: layer 0 (front) is top-left; deeper layers shift right/down.
      int ox = (layers - 1 - li) * layerOffset;
      int oy = ox;

      // Read source pixels (premultiplied BGRA).
      int srcW = wb.PixelWidth;
      int srcH = wb.PixelHeight;
      var src = new byte[srcW * srcH * 4];
      using (var stream = wb.PixelBuffer.AsStream())
        stream.Read(src, 0, src.Length);

      // Composite with Porter-Duff "src over dst", honouring layer opacity.
      for (int sy = 0; sy < Math.Min(srcH, totalSize - oy); sy++) {
        int dy = oy + sy;
        for (int sx = 0; sx < Math.Min(srcW, totalSize - ox); sx++) {
          int dx = ox + sx;
          int si = (sy * srcW + sx) * 4;
          int di = (dy * totalSize + dx) * 4;

          // Source pixel (premultiplied) scaled by layer opacity.
          float sb = (src[si] / 255f) * op;
          float sg = (src[si + 1] / 255f) * op;
          float sr = (src[si + 2] / 255f) * op;
          float sa = (src[si + 3] / 255f) * op;

          // Destination pixel (premultiplied).
          float db = dst[di] / 255f;
          float dg = dst[di + 1] / 255f;
          float dr = dst[di + 2] / 255f;
          float da = dst[di + 3] / 255f;

          // src-over: out = src + dst*(1-srcA)
          float oa = sa + da * (1f - sa);
          float outB = sb + db * (1f - sa);
          float outG = sg + dg * (1f - sa);
          float outR = sr + dr * (1f - sa);

          dst[di] = (byte)(outB * 255f + 0.5f);
          dst[di + 1] = (byte)(outG * 255f + 0.5f);
          dst[di + 2] = (byte)(outR * 255f + 0.5f);
          dst[di + 3] = (byte)(oa * 255f + 0.5f);
        }
      }
    }

    var sb2 = new SoftwareBitmap(BitmapPixelFormat.Bgra8, totalSize, totalSize,
                                  BitmapAlphaMode.Premultiplied);
    sb2.CopyFromBuffer(dst.AsBuffer());
    return sb2;
  }

  private async Task BeginItemDragAsync(ListViewItem container, PointerRoutedEventArgs e) {
    if (container.Content is not ShellItem pressedItem)
      return;

    // If the pressed item isn't already selected, make it the sole selection.
    if (!pressedItem.IsSelected) {
      ShellView.DeselectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(0, (uint)Items.Count));
      ShellView.SelectedItem = pressedItem;
    }

    // Snapshot selected items and their paths.
    var dragItems = ShellView.SelectedItems.OfType<ShellItem>().ToList();
    var paths = dragItems.Select(i => i.FullPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
    if (paths.Count == 0)
      return;

    _dragItemCount = dragItems.Count;

    // Build the shell-style drag image from item icons.
    var dragBitmap = BuildDragSoftwareBitmap(dragItems);

    // Wire DragStarting on the container once to supply StorageItems and drag UI.
    TypedEventHandler<UIElement, DragStartingEventArgs>? handler = null;
    handler = (_, args) => {
      container.DragStarting -= handler;
      args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

      // Shell drag image — set before any async work so WinUI uses it immediately.
      if (dragBitmap is not null) {
        args.DragUI.SetContentFromSoftwareBitmap(dragBitmap);
      }

      args.Data.SetDataProvider(StandardDataFormats.StorageItems, async request => {
        var deferral = request.GetDeferral();
        try {
          var tasks = paths.Select<string, Task<IStorageItem?>>(async p => {
            try {
              return Directory.Exists(p)
                  ? await StorageFolder.GetFolderFromPathAsync(p)
                  : (IStorageItem)await StorageFile.GetFileFromPathAsync(p);
            } catch { return null; }
          });
          request.SetData((await Task.WhenAll(tasks)).OfType<IStorageItem>().ToList());
        } finally { deferral.Complete(); }
      });
    };
    container.DragStarting += handler;

    var result = await container.StartDragAsync(e.GetCurrentPoint(container));

    _dragItemCount = 0;

    // External Move: surgically remove the dragged items — no full reload needed.
    if (!_internalDropHandled && result == DataPackageOperation.Move) {
      foreach (var p in paths) {
        var gone = Items.FirstOrDefault(i =>
            string.Equals(i.FullPath, p, StringComparison.OrdinalIgnoreCase));
        if (gone is not null)
          Items.Remove(gone);
      }
    }
    _internalDropHandled = false;
  }

  private void OnShellViewDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) {
    // Kept for compatibility; actual completion is handled in BeginItemDragAsync.
    ClearDragTarget();
  }

  private (ShellItem? Item, ListViewItem? Container) GetItemUnderDragPointer(DragEventArgs e) {
    // e.OriginalSource during DragOver is the ListView itself, not the child under
    // the pointer. Use position-based hit-testing to find the real ListViewItem.
    var posInShellView = e.GetPosition(ShellView);

    // FindElementsInHostCoordinates expects root (XamlRoot) coordinates.
    var root = XamlRoot?.Content as UIElement;
    if (root is null)
      return (null, null);

    var transform = ShellView.TransformToVisual(root);
    var rootPos = transform.TransformPoint(posInShellView);

    var hits = VisualTreeHelper.FindElementsInHostCoordinates(rootPos, ShellView,
                                                               includeAllElements: false);
    foreach (var hit in hits) {
      var dep = hit as DependencyObject;
      while (dep is not null and not ListViewItem and not ListView)
        dep = VisualTreeHelper.GetParent(dep);
      if (dep is ListViewItem lvi && lvi.Content is ShellItem item)
        return (item, lvi);
    }
    return (null, null);
  }

  private void OnShellViewDragOver(object sender, DragEventArgs e) {
    if (!e.DataView.Contains(StandardDataFormats.StorageItems)) {
      e.AcceptedOperation = DataPackageOperation.None;
      return;
    }

    var (target, container) = GetItemUnderDragPointer(e);

    // Update per-item hover + drop-target highlight.
    if (container != _currentDragContainer) {
      ClearDragTarget();
      if (container is not null) {
        VisualStateManager.GoToState(container, "PointerOver", true);
        _currentDragContainer = container;
      }
      if (target is { IsFolder: true }) {
        target.IsDropTarget = true;
        _currentDragTarget = target;
        SetContainerDropTarget(container, true);
      }
    }

    // Reject drops directly onto a file.
    if (target is { IsFolder: false }) {
      e.AcceptedOperation = DataPackageOperation.None;
      e.DragUIOverride.Caption = string.Empty;
      e.DragUIOverride.IsGlyphVisible = false;
      e.Handled = true;
      return;
    }

    bool copy = IsCtrlDown();
    e.AcceptedOperation = copy ? DataPackageOperation.Copy : DataPackageOperation.Move;
    e.DragUIOverride.IsGlyphVisible = true;
    e.DragUIOverride.IsCaptionVisible = true;

    string countLabel = _dragItemCount > 1 ? $"{_dragItemCount} items" : "1 item";
    string verb = copy ? "Copy" : "Move";
    e.DragUIOverride.Caption = target is { IsFolder: true }
        ? $"{verb} {countLabel} to \"{target.Name}\""
        : $"{verb} {countLabel} here";
    e.Handled = true;
  }

  private void OnShellViewDragLeave(object sender, DragEventArgs e) => ClearDragTarget();

  private async void OnShellViewDrop(object sender, DragEventArgs e) {
    if (!e.DataView.Contains(StandardDataFormats.StorageItems))
      return;

    var deferral = e.GetDeferral();

    // Capture the accepted operation NOW, before any await invalidates keyboard state.
    // AcceptedOperation was set authoritatively in OnShellViewDragOver.
    bool move = e.AcceptedOperation == DataPackageOperation.Move;

    try {
      var storageItems = await e.DataView.GetStorageItemsAsync();
      if (storageItems.Count == 0)
        return;

      var (target, _) = GetItemUnderDragPointer(e);
      var destPath = target is { IsFolder: true } ? target.FullPath : CurrentPath;

      if (string.IsNullOrEmpty(destPath) || !Directory.Exists(destPath))
        return;

      // No-op: moving items that are already in the destination.
      if (move && storageItems.All(i =>
              string.Equals(
                  Path.GetDirectoryName(i.Path), destPath,
                  StringComparison.OrdinalIgnoreCase)))
        return;

      var sourcePaths = storageItems.Select(i => i.Path).ToList();
      var hwnd = GetOwnerHwnd();
      var (removedPaths, addedPaths) =
          await NativeShell.ShellFileOperationAsync(sourcePaths, destPath, move, hwnd, IsDarkMode());

      _internalDropHandled = true;
      ApplyDropChanges(removedPaths, addedPaths, destPath);
    } finally {
      ClearDragTarget();
      deferral.Complete();
    }
  }

  // ── Keyboard copy / cut / paste ──────────────────────────────────────────

  private void OnShellViewKeyDown(object sender, KeyRoutedEventArgs e) {
    // Don't intercept shortcuts when the user is typing in a text field.
    if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
      return;

    var ctrl = Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
    bool ctrlDown = ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    if (!ctrlDown)
      return;

    switch (e.Key) {
      case Windows.System.VirtualKey.C:
        _ = CopySelectedToClipboardAsync(cut: false);
        e.Handled = true;
        break;
      case Windows.System.VirtualKey.X:
        _ = CopySelectedToClipboardAsync(cut: true);
        e.Handled = true;
        break;
      case Windows.System.VirtualKey.V:
        _ = PasteFromClipboardAsync();
        e.Handled = true;
        break;
    }
  }

  private async Task CopySelectedToClipboardAsync(bool cut) {
    var selected = ShellView.SelectedItems.OfType<ShellItem>().ToList();
    if (selected.Count == 0)
      return;

    // Resolve real StorageFile/StorageFolder objects so the clipboard
    // payload is fully compatible with Explorer and other apps.
    var storageItems = new List<IStorageItem>();
    foreach (var item in selected) {
      try {
        IStorageItem? si = item.IsFolder
            ? await StorageFolder.GetFolderFromPathAsync(item.FullPath)
            : await StorageFile.GetFileFromPathAsync(item.FullPath);
        if (si is not null)
          storageItems.Add(si);
      } catch { }
    }
    if (storageItems.Count == 0)
      return;

    // Clear any previous cut-ghost before applying the new state.
    ClearCutGhosts();

    var package = new DataPackage();
    package.RequestedOperation = cut
        ? DataPackageOperation.Move
        : DataPackageOperation.Copy;
    package.SetStorageItems(storageItems, readOnly: false);
    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

    // Always remember the paths and intent so paste works across navigation.
    _clipboardPaths = selected.Select(i => i.FullPath).ToList();
    _clipboardIsCut = cut;

    if (cut) {
      foreach (var item in selected)
        item.IsCut = true;
      _clipboardCutPaths = _clipboardPaths.ToList();
    } else {
      _clipboardCutPaths = null;
    }
  }

  private async Task PasteFromClipboardAsync() {
    if (string.IsNullOrEmpty(CurrentPath))
      return;

    List<string>? sourcePaths = null;
    bool move = false;

    if (_clipboardPaths is { Count: > 0 }) {
      // Fast path: use the paths we stored ourselves — works across navigation
      // and avoids WinRT broker access issues.
      sourcePaths = _clipboardPaths;
      move = _clipboardIsCut;
    } else {
      // Fallback: clipboard was set by another app (e.g. Explorer).
      DataPackageView? view;
      try { view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent(); } catch { return; }

      if (!view.Contains(StandardDataFormats.StorageItems))
        return;

      IReadOnlyList<IStorageItem> storageItems;
      try { storageItems = await view.GetStorageItemsAsync(); } catch { return; }
      if (storageItems.Count == 0)
        return;

      sourcePaths = storageItems.Select(i => i.Path).ToList();
      move = view.RequestedOperation == DataPackageOperation.Move;
    }

    var hwnd = GetOwnerHwnd();
    var (removedPaths, addedPaths) =
        await NativeShell.ShellFileOperationAsync(sourcePaths, CurrentPath, move, hwnd, IsDarkMode());

    ApplyDropChanges(removedPaths, addedPaths, CurrentPath);

    // After a cut-paste clear everything (clipboard + ghosts), matching Explorer.
    if (move) {
      try { Windows.ApplicationModel.DataTransfer.Clipboard.Clear(); } catch { }
      ClearClipboardState();
    }
  }

  // Clears only the IsCut ghost visuals on visible items — does NOT touch
  // _clipboardPaths so paste still works after navigation.
  private void ClearCutGhosts() {
    if (_clipboardCutPaths is null)
      return;
    var cutSet = new HashSet<string>(_clipboardCutPaths, StringComparer.OrdinalIgnoreCase);
    foreach (var item in Items)
      if (item.IsCut && cutSet.Contains(item.FullPath))
        item.IsCut = false;
    _clipboardCutPaths = null;
  }

  // Full teardown after a cut-paste: clears ghosts AND the stored path list.
  private void ClearClipboardState() {
    ClearCutGhosts();
    _clipboardPaths = null;
    _clipboardIsCut = false;
  }

  private IntPtr GetOwnerHwnd() {
    try {
      var window = Microsoft.UI.Xaml.Window.Current;
      if (window is not null)
        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    } catch { }
    return IntPtr.Zero;
  }

  private bool IsDarkMode() {
    try {
      return ActualTheme == ElementTheme.Dark;
    } catch { }
    return false;
  }

  /// <summary>
  /// Surgically removes items that were moved away and inserts items that
  /// arrived in <see cref="CurrentPath"/> — no full directory reload.
  /// </summary>
  private void ApplyDropChanges(List<string> removedPaths, List<string> addedPaths, string destPath) {
    // Remove items whose source was in the current view.
    if (removedPaths.Count > 0) {
      var removedSet = new HashSet<string>(removedPaths, StringComparer.OrdinalIgnoreCase);
      var toRemove = Items.Where(i => removedSet.Contains(i.FullPath)).ToList();
      foreach (var item in toRemove)
        Items.Remove(item);
    }

    // Insert items that landed in the current folder.
    if (addedPaths.Count > 0 &&
        string.Equals(destPath, CurrentPath, StringComparison.OrdinalIgnoreCase)) {
      var newItems = addedPaths
          .Select(CreateShellItemForPath)
          .OfType<ShellItem>()
          .ToList();

      if (newItems.Count > 0) {
        var size = ThumbnailSizeForMode(ViewMode);
        ApplyCachedIcons(newItems, size);
        foreach (var ni in newItems)
          InsertSortedIntoItems(ni);
        EnqueueThumbnailsForItems(newItems, size);
      }
    }
  }

  /// <summary>
  /// Creates a <see cref="ShellItem"/> from a file-system path without
  /// doing any shell or COM work — icon loading is handled separately.
  /// </summary>
  private static ShellItem? CreateShellItemForPath(string path) {
    try {
      if (Directory.Exists(path)) {
        var di = new DirectoryInfo(path);
        return new ShellItem {
          Name = di.Name,
          FullPath = path,
          ItemType = "File folder",
          IsFolder = true,
          IsHidden = (di.Attributes & System.IO.FileAttributes.Hidden) != 0,
        };
      }
      if (File.Exists(path)) {
        var fi = new FileInfo(path);
        var ext = fi.Extension;
        var typeName = ext.Length <= 1 ? "File" : ext[1..].ToUpperInvariant() + " file";
        return new ShellItem {
          Name = fi.Name,
          FullPath = path,
          ItemType = typeName,
          IsFolder = false,
          IsHidden = (fi.Attributes & System.IO.FileAttributes.Hidden) != 0,
          Size = NativeShell.FormatSize(fi.Length),
          SizeBytes = fi.Length,
          DateModified = fi.LastWriteTime,
        };
      }
    } catch { }
    return null;
  }

  /// <summary>
  /// Inserts <paramref name="item"/> into <see cref="Items"/> at the position
  /// that preserves the current sort order (folders before files, then by column).
  /// </summary>
  private void InsertSortedIntoItems(ShellItem item) {
    Func<ShellItem, object?> key = _sortColumn switch {
      "Date" => it => (object?)it.DateModified,
      "Type" => it => it.ItemType,
      "Size" => it => it.SizeBytes,
      _ => it => it.Name,
    };

    for (int i = 0; i < Items.Count; i++) {
      var existing = Items[i];
      // Folders always sort before files.
      if (item.IsFolder && !existing.IsFolder) { Items.Insert(i, item); return; }
      if (!item.IsFolder && existing.IsFolder)
        continue;
      // Same tier: compare by sort key.
      int cmp = Comparer<object?>.Default.Compare(key(item), key(existing));
      if (_sortAscending ? cmp <= 0 : cmp >= 0) { Items.Insert(i, item); return; }
    }
    Items.Add(item);
  }
}
