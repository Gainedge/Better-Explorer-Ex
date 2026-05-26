using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

  /// <summary>Raised when a search starts (non-null query) or is cleared (null).</summary>
  public event EventHandler<string?>? SearchQueryChanged;

  /// <summary>
  /// Raised with <c>true</c> when an async navigation or search starts,
  /// and with <c>false</c> when it completes successfully.
  /// Superseded (cancelled) navigations do NOT fire false — the replacement
  /// already fired true, so the busy state remains correct.
  /// </summary>
  public event EventHandler<bool>? BusyChanged;

  /// <summary>Raised whenever the list-view selection changes.</summary>
  public event EventHandler? SelectionChanged;

  /// <summary>Raised whenever the internal clipboard content changes (after copy/cut/paste).</summary>
  public event EventHandler? ClipboardChanged;

  /// <summary>True when at least one item is selected.</summary>
  public bool HasSelection => ShellView.SelectedItems.Count > 0;

  /// <summary>True when exactly one item is selected.</summary>
  public bool SelectionIsSingle => ShellView.SelectedItems.Count == 1;

  /// <summary>True when more than one item is selected.</summary>
  public bool SelectionIsMulti => ShellView.SelectedItems.Count > 1;

  /// <summary>True when exactly one folder is selected.</summary>
  public bool SelectionIsSingleFolder =>
      ShellView.SelectedItems.Count == 1 &&
      ShellView.SelectedItems[0] is ShellItem { IsFolder: true };

  /// <summary>Raised whenever sort column or direction changes (column-header tap or toolbar).</summary>
  public event EventHandler? SortChanged;

  /// <summary>Raised whenever the group-by column changes.</summary>
  public event EventHandler? GroupChanged;

  // ── Tree-sync events (forwarded to ShellTreeView by ExplorerBrowser) ─────

  /// <summary>Raised when a new drive root is detected (real FS or shell notification).</summary>
  public event EventHandler<string>? TreeDriveAdded;
  /// <summary>Raised when a drive root is removed.</summary>
  public event EventHandler<string>? TreeDriveRemoved;
  /// <summary>Raised when a folder is created inside any watched directory.</summary>
  public event EventHandler<string>? TreeFolderCreated;
  /// <summary>Raised when a folder is deleted from any watched directory.</summary>
  public event EventHandler<string>? TreeFolderDeleted;
  /// <summary>Raised when a folder is renamed. Item1 = old path, Item2 = new path.</summary>
  public event EventHandler<(string OldPath, string NewPath)>? TreeFolderRenamed;

  public string SortColumn    => _sortColumn;
  public bool   SortAscending => _sortAscending;
  /// <summary>Current group-by column key, or empty string when grouping is off.</summary>
  public string GroupColumn   => _groupColumn;

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
  // Tracks the path passed to Navigate/NavigateToKnownFolder before the control is
  // loaded so that ShellListView_Loaded does not override it with the default C:\ path.
  private string? _pendingNavigatePath;
  // ── Search state ─────────────────────────────────────────────────────────
  // CTS for in-flight search so typing a new character cancels the previous run.
  private CancellationTokenSource _searchCts = new();
  // True when the list is showing search results rather than a plain directory.
  private bool _isSearchActive;
  private const int ThumbConcurrency = 8;

  // ── Filesystem watcher ───────────────────────────────────────────────────
  private FileSystemWatcher? _fsWatcher;
  // Shell-namespace watcher for virtual folders (::{GUID}) — e.g. "This PC", "Network".
  private ShellChangeWatcher? _shellWatcher;
  // When set, we are waiting for a newly created item in this folder to appear so we can auto-rename it.
  private string? _renameOnNewItemFolder;
  private HashSet<string>? _renameOnNewItemSnapshot;
  // Suppresses the name-expansion popup while we are waiting for a new item's
  // container to be rendered before starting rename.
  private bool _suppressNameExpansion;

  // Saved popup state from the last NotifyDeactivated() call, so it can be
  // restored immediately on NotifyActivated() before the layout pass runs.
  private record struct PopupSnapshot(
      double Left, double Top, double Width,
      double OverflowHeight,
      ShellItem Item, ListViewItem Container);
  private PopupSnapshot? _savedPopupState;
  // Set to true during NotifyDeactivated so CollapseAllNameExpansions
  // does not clear _savedPopupState while we are saving it.
  private bool _savingDeactivationSnapshot;
  // Set to true after NotifyDeactivated has saved a snapshot, and cleared
  // after NotifyActivated consumes it.  Prevents any intermediate
  // CollapseAllNameExpansions (focus-loss, etc.) from discarding the cache.
  private bool _snapshotPendingRestore;
  // Debounce: shell notifications arrive in bursts (e.g. USB enumeration fires multiple
  // events); collapse them into a single Refresh() after a quiet period.
  private DispatcherTimer? _shellRefreshDebounce;
  // Per-path debounce: rapid Changed events (e.g. file copy in progress) are
  // coalesced into one update after a 350 ms quiet period.
  private readonly Dictionary<string, DispatcherTimer> _changeDebounce = new(StringComparer.OrdinalIgnoreCase);
  private const int CloudThumbRetryMax = 16;   // ~40 s total with progressive back-off
  private const int CloudThumbRetryBaseMs = 500;  // delay = min(retry * 500, 4000)
  private const int CloudThumbRetryMaxMs = 4000;
  private Channel<(ShellItem Item, uint Size, int Retry)> _thumbChannel = CreateChannel();

  // ── Overlay icon channel ─────────────────────────────────────────────────
  // Separate lightweight channel for shell overlay icons (shortcut arrow, OneDrive sync,
  // Git status from TortoiseGit, etc.).  Workers are cancelled together with the
  // thumbnail workers so overlays don't outlive the current navigation.
  private Channel<ShellItem> _overlayChannel = CreateOverlayChannel();
  private const int OverlayConcurrency = 2;

  private static Channel<ShellItem> CreateOverlayChannel() =>
      Channel.CreateBounded<ShellItem>(new BoundedChannelOptions(512) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false
      });

  // Per-overlay bitmap cache. The pixel array from NativeShell._overlayPixelCache is reused
  // by reference for the same overlay slot, so we key on object identity to deduplicate bitmaps.
  private static readonly Dictionary<int, WriteableBitmap?> _overlayBitmapCache = new();

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

  // ── Rename popup (created in code — Popup children lose x:Name in WinUI 3) ──
  private readonly Microsoft.UI.Xaml.Controls.Primitives.Popup _renamePopup  = new();
  private readonly TextBox _renameTextBox = new();

  // Stored so that AddHandler/RemoveHandler use the same delegate instance.
  private KeyEventHandler? _keyDownHandler;

  private string _sortColumn = "Name";
  private bool _sortAscending = true;
  private string _groupColumn = string.Empty;  // empty = no grouping
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

    // Apply the custom template (no delete button) and wire events.
    _renameTextBox.Style = (Style)Resources["RenameTextBoxStyle"];
    _renameTextBox.KeyDown   += RenameTextBox_KeyDown;
    _renameTextBox.LostFocus += RenameTextBox_LostFocus;
    _renamePopup.Child = _renameTextBox;
  }

  private void ShellListView_Loaded(object sender, RoutedEventArgs e) {
    // Only fall back to C:\ if no navigation has been requested yet (neither completed
    // nor pending). If Navigate/NavigateToKnownFolder was already called before the
    // control finished loading, the async LoadDirectory is already in flight — don't
    // cancel it by kicking off another one.
    if (string.IsNullOrEmpty(CurrentPath) && _pendingNavigatePath is null)
      Navigate(@"C:\");

    // Pre-load shell extension DLLs in the background so the first right-click is
    // fast. This does a real (throw-away) QueryContextMenu on the Desktop folder
    // and on notepad.exe, covering both folder-background and per-file handlers.
    _ = ShellContextMenuService.WarmUpAsync();

    // Add the rename popup to the visual tree so it inherits theme resources.
    if (!DragSelectGrid.Children.Contains(_renamePopup))
      DragSelectGrid.Children.Add(_renamePopup);

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
    if (XamlRoot?.Content is UIElement root) {
      _keyDownHandler ??= new KeyEventHandler(OnShellViewKeyDown);
      root.AddHandler(KeyDownEvent, _keyDownHandler, handledEventsToo: true);
    }

    // Reposition the name-expansion popup whenever the list area is resized
    // (e.g. window resize, pane splitter drag) so it tracks the selected item.
    DragSelectGrid.SizeChanged += OnDragSelectGridSizeChanged;
  }

  private void ShellListView_Unloaded(object sender, RoutedEventArgs e) {
    StopFolderWatcher();

    if (XamlRoot?.Content is UIElement root && _keyDownHandler is not null)
      root.RemoveHandler(KeyDownEvent, _keyDownHandler);

    DragSelectGrid.SizeChanged -= OnDragSelectGridSizeChanged;
  }

  private void OnDragSelectGridSizeChanged(object sender, SizeChangedEventArgs e) {
    // If the popup is open, recompute its position against the item's new screen
    // coordinates — the item may have shifted during a window/pane resize.
    if (NameExpansionPopup.IsOpen)
      UpdateNameExpansion();
  }

  // ── Navigation API ───────────────────────────────────────────────────────

  // ── Filesystem watcher helpers ───────────────────────────────────────────

  private void StartFolderWatcher(string path) {
    StopFolderWatcher();

    // Virtual shell-namespace paths (::{GUID}) have no backing directory;
    // use SHChangeNotifyRegister instead of FileSystemWatcher.
    if (path.StartsWith("::", StringComparison.Ordinal)) {
      _shellWatcher = new ShellChangeWatcher(path);
      _shellWatcher.Changed += OnShellWatcher_Changed;
      _shellWatcher.Start();
      return;
    }

    if (!Directory.Exists(path))
      return;
    try {
      _fsWatcher = new FileSystemWatcher(path) {
        NotifyFilter = NotifyFilters.FileName
                     | NotifyFilters.DirectoryName
                     | NotifyFilters.LastWrite
                     | NotifyFilters.Size,
        IncludeSubdirectories = false,
        EnableRaisingEvents = true
      };
      _fsWatcher.Created += OnWatcher_Created;
      _fsWatcher.Deleted += OnWatcher_Deleted;
      _fsWatcher.Renamed += OnWatcher_Renamed;
      _fsWatcher.Changed += OnWatcher_Changed;
      _fsWatcher.Error   += OnWatcher_Error;
    } catch { /* network share or permission denied — watch silently fails */ }
  }

  private void StopFolderWatcher() {
    // Stop FileSystemWatcher (real FS paths).
    if (_fsWatcher is not null) {
      _fsWatcher.EnableRaisingEvents = false;
      _fsWatcher.Created -= OnWatcher_Created;
      _fsWatcher.Deleted -= OnWatcher_Deleted;
      _fsWatcher.Renamed -= OnWatcher_Renamed;
      _fsWatcher.Changed -= OnWatcher_Changed;
      _fsWatcher.Error   -= OnWatcher_Error;
      _fsWatcher.Dispose();
      _fsWatcher = null;
    }

    // Stop ShellChangeWatcher (virtual paths).
    if (_shellWatcher is not null) {
      _shellWatcher.Changed -= OnShellWatcher_Changed;
      _shellWatcher.Dispose();
      _shellWatcher = null;
    }

    // Cancel the shell-notification debounce timer.
    if (_shellRefreshDebounce is not null) {
      _shellRefreshDebounce.Stop();
      _shellRefreshDebounce = null;
    }

    // Cancel all FS-path debounce timers.
    foreach (var t in _changeDebounce.Values)
      t.Stop();
    _changeDebounce.Clear();
  }

  // Each watcher event is raised on a thread-pool thread; marshal everything
  // back to the UI DispatcherQueue before touching Items.

  // ── Shell-namespace (virtual folder) change handler ──────────────────────

  private void OnShellWatcher_Changed(object? sender, ShellChangeEventArgs e) {
    // This is called on the STA message-pump thread; dispatch to the UI thread.
    DispatcherQueue.TryEnqueue(() => {
      // Raise targeted tree-sync events for drive add/remove.
      switch (e.EventType) {
        case ShellChangeType.DriveAdd when e.Path is not null:
          TreeDriveAdded?.Invoke(this, e.Path);
          break;
        case ShellChangeType.DriveRemoved when e.Path is not null:
          TreeDriveRemoved?.Invoke(this, e.Path);
          break;
        case ShellChangeType.MkDir when e.Path is not null:
          TreeFolderCreated?.Invoke(this, e.Path);
          break;
        case ShellChangeType.RmDir when e.Path is not null:
          TreeFolderDeleted?.Invoke(this, e.Path);
          break;
        case ShellChangeType.RenameFolder when e.Path is not null && e.Path2 is not null:
          TreeFolderRenamed?.Invoke(this, (e.Path, e.Path2));
          break;
      }
      // Debounce: USB enumeration and similar events come in rapid bursts.
      // Collapse them into a single Refresh() after 500 ms of quiet.
      if (_shellRefreshDebounce is null) {
        _shellRefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _shellRefreshDebounce.Tick += (_, _) => {
          _shellRefreshDebounce?.Stop();
          _shellRefreshDebounce = null;
          Refresh();
        };
      }
      _shellRefreshDebounce.Stop();
      _shellRefreshDebounce.Start();
    });
  }


  private void OnWatcher_Created(object sender, FileSystemEventArgs e) {
    DispatcherQueue.TryEnqueue(() => {
      // Skip if we navigated away since the event was raised.
      if (!string.Equals(Path.GetDirectoryName(e.FullPath), CurrentPath,
              StringComparison.OrdinalIgnoreCase))
        return;
      // Item may already exist (rapid create+rename pairs can fire twice).
      if (Items.Any(i => string.Equals(i.FullPath, e.FullPath, StringComparison.OrdinalIgnoreCase)))
        return;
      var item = NativeShell.GetSingleItemMetadata(e.FullPath);
      if (item is null)
        return;
      // Insert in sorted position relative to current sort.
      var merged = Items.ToList();
      merged.Add(item);
      merged = SortItems(merged);
      int idx = merged.IndexOf(item);
      Items.Insert(idx, item);
      // Icon will be loaded the next time the container becomes visible; queue
      // a thumbnail for it now so it isn't blank longer than necessary.
      // Capture ViewMode on the UI thread before jumping to the thread pool,
      // since DependencyProperty access requires STA/UI-thread affinity.
      var viewMode = ViewMode;
      _ = Task.Run(() => {
        var size = ThumbnailSizeForMode(viewMode);
        NativeShell.TryGetShellHBitmap(item.FullPath, size, NativeShell.SIIGBF.ResizeToFit);
      });
      ApplyGrouping();
      // Notify tree if this is a folder.
      if (item.IsFolder)
        TreeFolderCreated?.Invoke(this, e.FullPath);
    });
  }

  private void OnWatcher_Deleted(object sender, FileSystemEventArgs e) {
    DispatcherQueue.TryEnqueue(() => {
      var item = Items.FirstOrDefault(i =>
          string.Equals(i.FullPath, e.FullPath, StringComparison.OrdinalIgnoreCase));
      if (item is null) {
        // Not in the list (e.g. navigated away), but still notify tree.
        if (Directory.Exists(e.FullPath) == false && !Path.HasExtension(e.FullPath))
          TreeFolderDeleted?.Invoke(this, e.FullPath);
        return;
      }
      bool wasFolder = item.IsFolder;
      Items.Remove(item);
      ApplyGrouping();
      if (wasFolder)
        TreeFolderDeleted?.Invoke(this, e.FullPath);
    });
  }

  private void OnWatcher_Renamed(object sender, RenamedEventArgs e) {
    DispatcherQueue.TryEnqueue(() => {
      var item = Items.FirstOrDefault(i =>
          string.Equals(i.FullPath, e.OldFullPath, StringComparison.OrdinalIgnoreCase));
      if (item is null) {
        // Might have been created outside the watched window — treat as Create.
        OnWatcher_Created(sender, e);
        return;
      }
      bool wasFolder = item.IsFolder;
      item.Name = e.Name ?? Path.GetFileName(e.FullPath);
      item.FullPath = e.FullPath;
      // Re-sort: the item may have moved to a different position in the list.
      var sorted = SortItems(Items.ToList());
      int oldIdx = Items.IndexOf(item);
      int newIdx = sorted.IndexOf(item);
      if (oldIdx != newIdx) {
        Items.Remove(item);
        Items.Insert(newIdx > Items.Count ? Items.Count : newIdx, item);
      }
      ApplyGrouping();
      if (wasFolder)
        TreeFolderRenamed?.Invoke(this, (e.OldFullPath, e.FullPath));
    });
  }

  private void OnWatcher_Changed(object sender, FileSystemEventArgs e) {
    DispatcherQueue.TryEnqueue(() => {
      // Debounce: reset a per-path timer so that bursts (e.g. large file copy)
      // only issue one metadata read after the last event settles.
      if (!_changeDebounce.TryGetValue(e.FullPath, out var timer)) {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        var captured = e.FullPath;
        timer.Tick += (_, _) => {
          _changeDebounce.Remove(captured);
          timer.Stop();
          ApplyChangedMetadata(captured);
        };
        _changeDebounce[e.FullPath] = timer;
      }
      // Restart the timer on each new event.
      timer.Stop();
      timer.Start();
    });
  }

  private void ApplyChangedMetadata(string fullPath) {
    var item = Items.FirstOrDefault(i =>
        string.Equals(i.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
    if (item is null)
      return;
    var fresh = NativeShell.GetSingleItemMetadata(fullPath);
    if (fresh is null)
      return;
    item.DateModified = fresh.DateModified;
    item.Size = fresh.Size;
    item.SizeBytes = fresh.SizeBytes;
  }

  private void OnWatcher_Error(object sender, ErrorEventArgs e) {
    // Buffer overflow or network disconnect — fall back to a full reload.
    DispatcherQueue.TryEnqueue(Refresh);
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

    _pendingNavigatePath = path;
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
  /// Searches the currently-open folder using the Windows Search API (AQS).
  /// Results are streamed into the ListView in batches as they are found.
  /// Passing an empty or whitespace query clears the search and restores the folder.
  /// </summary>
  public async void SearchCurrentFolder(string query) {
    if (string.IsNullOrWhiteSpace(query)) {
      ClearSearch();
      return;
    }

    var dispatcherQueue = DispatcherQueue;
    if (dispatcherQueue == null) return;

    _searchCts.Cancel();
    _searchCts = new CancellationTokenSource();
    var ct = _searchCts.Token;

    var wasAlreadySearching = _isSearchActive;
    _isSearchActive = true;
    // Push the current folder onto the back stack the first time a search starts
    // so that Back navigation exits the search and returns here.
    if (!wasAlreadySearching && !string.IsNullOrEmpty(CurrentPath)) {
      _backStack.Push(CurrentPath);
      _forwardStack.Clear();
      CanGoBack    = true;
      CanGoForward = false;
    }
    SearchQueryChanged?.Invoke(this, query);
    BusyChanged?.Invoke(this, true);
    CollapseAllNameExpansions();

    // Cancel any ongoing directory load and reset the list.
    _navCts.Cancel();
    _navCts = new CancellationTokenSource();
    RestartThumbnailWorker();
    Items.Clear();
    if (ShellView.ItemsSource != Items)
      ShellView.ItemsSource = Items;

    var size = ThumbnailSizeForMode(ViewMode);
    var currentPath = CurrentPath;
    const int batchSize = 10;

    var reader = NativeShell.SearchFolderStreamAsync(currentPath, query, ct);
    var buffer = new List<ShellItem>(batchSize);

    try {
      await foreach (var item in reader.ReadAllAsync(ct)) {
        buffer.Add(item);
        if (buffer.Count >= batchSize) {
          var batchToFlush = buffer.ToList();
          buffer.Clear();
          // ReadAllAsync uses ConfigureAwait(false) internally, so its continuations
          // may run on a thread-pool thread. Marshal the flush back to the UI thread
          // because WarmTypeIconCacheAsync creates WriteableBitmap objects and
          // Items.Add fires WinRT CollectionChanged — both require the dispatcher thread.
          var tcs = new TaskCompletionSource();
          dispatcherQueue.TryEnqueue(async () => {
            try { await FlushSearchBatchAsync(batchToFlush, size, ct); }
            catch (Exception ex) { tcs.TrySetException(ex); return; }
            tcs.TrySetResult();
          });
          await tcs.Task;
        }
      }

      // Flush any remaining items after the stream completes.
      if (buffer.Count > 0) {
        var batchToFlush = buffer.ToList();
        buffer.Clear();
        var tcs = new TaskCompletionSource();
        dispatcherQueue.TryEnqueue(async () => {
          try { await FlushSearchBatchAsync(batchToFlush, size, ct); }
          catch (Exception ex) { tcs.TrySetException(ex); return; }
          tcs.TrySetResult();
        });
        await tcs.Task;
      }
    } catch (OperationCanceledException) {
      return;
    }

    if (ct.IsCancellationRequested)
      return;

    // Sort the accumulated results and refresh grouping once, at the end.
    var sorted = SortItems(Items.ToList());
    Items.Clear();
    Items.AddRange(sorted);
    ApplyGrouping();
    UpdateSortIndicators();
    BusyChanged?.Invoke(this, false);
  }

  /// <summary>Clears any active search and reloads the current folder.</summary>
  public void ClearSearch() {
    if (!_isSearchActive)
      return;
    _isSearchActive = false;
    _searchCts.Cancel();
    SearchQueryChanged?.Invoke(this, null);
    LoadDirectory(CurrentPath);
  }

  /// <summary>
  /// Warms icons for <paramref name="batch"/>, stamps them, then appends every
  /// item to <see cref="Items"/> so they appear in the ListView immediately.
  /// </summary>
  private async Task FlushSearchBatchAsync(List<ShellItem> batch, uint size, CancellationToken ct) {
    try {
      await WarmTypeIconCacheAsync(batch, size, ct);
    } catch (OperationCanceledException) {
      throw;
    }
    ct.ThrowIfCancellationRequested();
    ApplyCachedIcons(batch, size);
    foreach (var item in batch)
      Items.Add(item);
  }

  /// <summary>
  /// Moves keyboard focus onto the inner ListView so selected items render in
  /// the <c>Selected</c> visual state (accent colour) rather than the dimmer
  /// <c>SelectedUnfocused</c> state.
  /// </summary>
  public void FocusListView() => ShellView.Focus(FocusState.Programmatic);

  /// <summary>
  /// Called when the tab hosting this list is being hidden (switched away from).
  /// Saves the current popup geometry so it can be restored instantly on the next activation.
  /// The snapshot is taken from the live expanded-item fields, which remain valid even
  /// if the popup was already closed by a focus-loss event during the tab switch.
  /// </summary>
  public void NotifyDeactivated()
  {
    // Snapshot from the last known good popup state stored in UpdateNameExpansion.
    // _savedPopupState is already written there; nothing more to do unless it was
    // cleared by CollapseAllNameExpansions during the tab-switch event sequence.
    // In that case, rebuild from the expanded-item fields if they are still set.
    if (_savedPopupState is null && _expandedItem is not null && _expandedContainer is not null)
    {
      _savingDeactivationSnapshot = true;
      try
      {
        _savedPopupState = new PopupSnapshot(
            NameExpansionPopup.HorizontalOffset,
            NameExpansionPopup.VerticalOffset,
            NameExpansionCard.Width,
            NameExpansionOverflowFill.Height,
            _expandedItem,
            _expandedContainer);
      }
      finally
      {
        _savingDeactivationSnapshot = false;
      }
    }

    // Mark that a snapshot is pending so intermediate CollapseAllNameExpansions
    // calls (focus-loss, etc.) do not discard it before NotifyActivated runs.
    if (_savedPopupState is not null)
      _snapshotPendingRestore = true;

    // Hide the popup while the tab is not visible (without clearing the cache).
    NameExpansionPopup.IsOpen = false;
  }

  /// <summary>
  /// Called when the tab that hosts this list view becomes the active tab.
  /// Restores the saved popup state at the saved position immediately.
  /// The coordinates saved at deactivation are still valid because the
  /// ShellListView layout does not change while the tab is hidden.
  /// </summary>
  public void NotifyActivated()
  {
    FocusListView();
    _snapshotPendingRestore = false;   // snapshot is about to be consumed

    if (_savedPopupState is { } snap &&
        ShellView.SelectedItems.Count == 1 &&
        ShellView.SelectedItems.Contains(snap.Item))
    {
      NameExpansionText.Text   = snap.Item.Name;
      NameExpansionCard.Width  = snap.Width;
      NameExpansionOverflowFill.Height  = snap.OverflowHeight;
      NameExpansionOverflowFill2.Height = snap.OverflowHeight;
      snap.Item.IsLabelHidden = true;
      VisualStateManager.GoToState(snap.Container, "NameExpanded", false);
      _expandedItem      = snap.Item;
      _expandedContainer = snap.Container;
      NameExpansionPopup.HorizontalOffset = snap.Left;
      NameExpansionPopup.VerticalOffset   = snap.Top;
      NameExpansionPopup.IsOpen = true;
    }
  }

  public async void NavigateToKnownFolder(Guid folderId) {
    var virtualPath = $"::{folderId:B}";
    // Cancel any active search so its results don't bleed into the new folder.
    if (_isSearchActive) {
      _isSearchActive = false;
      _searchCts.Cancel();
      _searchCts = new CancellationTokenSource();
      SearchQueryChanged?.Invoke(this, null);
    }
    // Stop watching the outgoing folder immediately.
    StopFolderWatcher();

    await ApplyFolderSettings(virtualPath);

    // Break any existing grouped CollectionViewSource binding before clearing items.
    if (ShellView.ItemsSource != Items)
      ShellView.ItemsSource = Items;

    // Don't re-navigate to the folder already shown
    if (string.Equals(virtualPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
      return;

    if (!string.IsNullOrEmpty(CurrentPath))
      _backStack.Push(CurrentPath);
    _forwardStack.Clear();
    _pendingNavigatePath = virtualPath;

    // Cancel any pending popup-wait from the previous navigation.
    _popupWaitCts.Cancel();
    _popupWaitCts = new CancellationTokenSource();

    // Cancel any previous navigation and start a fresh one.
    _navCts.Cancel();
    _navCts = new CancellationTokenSource();
    var ct = _navCts.Token;
    BusyChanged?.Invoke(this, true);

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
    ApplyGrouping();
    CurrentPath = virtualPath;
    _pendingNavigatePath = null;
    BusyChanged?.Invoke(this, false);
    PathChanged?.Invoke(this, CurrentPath);
    CanGoBack = _backStack.Count > 0;
    CanGoForward = _forwardStack.Count > 0;
    ApplyPendingSelection();
    // Watch the virtual folder for shell change notifications (drive add/remove, etc.).
    StartFolderWatcher(virtualPath);
  }

  // ── Directory loading ────────────────────────────────────────────────────

  private async void LoadDirectory(string path) {
    // Cancel any active search so its results don't bleed into the new folder.
    if (_isSearchActive) {
      _isSearchActive = false;
      _searchCts.Cancel();
      _searchCts = new CancellationTokenSource();
      SearchQueryChanged?.Invoke(this, null);
    }
    // Stop watching the outgoing folder immediately so stale events from the
    // previous directory do not race with the new navigation.
    StopFolderWatcher();

    await ApplyFolderSettings(path);

    // Break any existing grouped CollectionViewSource binding so the old grouped
    // layout is never rendered against the incoming folder's items.
    if (ShellView.ItemsSource != Items)
      ShellView.ItemsSource = Items;

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
    BusyChanged?.Invoke(this, true);

    RestartThumbnailWorker();
    Items.Clear();

    var size = ThumbnailSizeForMode(ViewMode);

    // Virtual shell paths (::) must be enumerated via the shell API, not FindFirstFileEx.
    if (path.StartsWith("::", StringComparison.Ordinal)) {
      List<ShellItem> kfItems;
      // Bare known-folder GUIDs use SHGetKnownFolderItem for reliable resolution.
      // Compound paths like ::{GUID}\foo.library-ms go through SHCreateItemFromParsingName.
      var stripped = path.Trim(':', '{', '}');
      if (Guid.TryParse(stripped, out var folderId))
        kfItems = await Task.Run(() => NativeShell.EnumerateKnownFolderChildren(folderId), ct);
      else
        kfItems = await Task.Run(() => NativeShell.EnumerateShellItemChildrenByPath(path), ct);
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
      ApplyGrouping();
      CurrentPath = path;
      _pendingNavigatePath = null;
      CanGoBack = _backStack.Count > 0;
      CanGoForward = _forwardStack.Count > 0;
      BusyChanged?.Invoke(this, false);
      PathChanged?.Invoke(this, path);
      UpdateSortIndicators();
      ApplyPendingSelection();
      // Start a ShellChangeWatcher for this virtual path so drive/media events are caught.
      StartFolderWatcher(path);
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
    ApplyGrouping();
    CurrentPath = path;
    _pendingNavigatePath = null;
    CanGoBack = _backStack.Count > 0;
    CanGoForward = _forwardStack.Count > 0;
    BusyChanged?.Invoke(this, false);
    PathChanged?.Invoke(this, path);
    UpdateSortIndicators();
    ApplyPendingSelection();
    // Watch the folder for live filesystem changes now that it is fully loaded.
    StartFolderWatcher(path);
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
  /// <summary>
  /// Returns <see langword="true"/> for folders whose icon varies per path (drives,
  /// virtual shell-namespace paths) and that must therefore use <c>IconOnly</c> rather
  /// than <c>ResizeToFit</c> — the shell does not produce a content-preview thumbnail
  /// for these items and would return a generic drive/folder icon at full cost.
  /// Regular folders are <em>not</em> per-item folders and go through the normal
  /// thumbnail pipeline so their content preview is displayed.
  /// </summary>
  private static bool IsPerItemFolder(ShellItem item) =>
      item.IsFolder && (
          // Virtual shell-namespace paths (e.g. ::{GUID}, ::{GUID}\sub)
          item.FullPath.StartsWith("::", StringComparison.Ordinal) ||
          // Drive roots: "C:\", "D:\", etc. (length == 3, last char is '\')
          (item.FullPath.Length == 3 && item.FullPath[1] == ':' && item.FullPath[2] == '\\') ||
          // UNC roots: "\\server\share\" and bare "\\server\"
          (item.FullPath.StartsWith("\\\\", StringComparison.Ordinal) &&
           item.FullPath.IndexOf('\\', 2) == item.FullPath.LastIndexOf('\\')));

  // Returns a type-cache key for an item.
  // All folders get a unique per-path key so shell-extension overlays (Git, SVN, …)
  // are never incorrectly shared between folders.
  // Non-folder files share a key by extension (safe: type icon is the same for all .txt, etc.)
  private static string TypeIconKey(ShellItem item) =>
      item.IsFolder
          ? ":folder:" + item.FullPath.TrimEnd('\\').ToLowerInvariant()
          : Path.GetExtension(item.FullPath).ToLowerInvariant();

  private void ApplyCachedIcons(List<ShellItem> items, uint size) {
    foreach (var item in items) {
      if (item.HasRealThumbnail || item.Icon != null)
        continue;
      var ext = TypeIconKey(item);
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
      var ext = TypeIconKey(item);
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
          var extKey = TypeIconKey(item);
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
      var ext = TypeIconKey(item);
      bool canUpgrade = !IsIconOnlyMode(ViewMode) && !IsPerItemFolder(item) &&
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
      var ext = TypeIconKey(item);
      if (_perFileIconExts.Contains(ext))
        continue;
      // Per-item folders (drives, virtual-path) are handled by the thumbnail
      // worker via IconOnly — exclude them from the ResizeToFit cache preload.
      if (IsPerItemFolder(item))
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
    SelectionChanged?.Invoke(this, EventArgs.Empty);
  }

  // How many px of the popup card sit inside the item's own bounds
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
    if (_suppressNameExpansion) return;
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

    // Cache the final geometry so NotifyActivated() can restore instantly.
    _savedPopupState = new PopupSnapshot(
        popupLeft, popupTop, popupWidth,
        NameExpansionOverflowFill.Height,
        selected, container);
  }

  private void CollapseAllNameExpansions() {
    NameExpansionPopup.IsOpen = false;
    // Only clear the snapshot on a genuine user-driven collapse — not while
    // we are building the deactivation snapshot, and not while a saved snapshot
    // is waiting to be restored on the next tab activation.
    if (!_savingDeactivationSnapshot && !_snapshotPendingRestore)
      _savedPopupState = null;
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
    var ext = TypeIconKey(item);
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

    // Overlay icons are independent of whether a real thumbnail is already loaded.
    // Enqueue before the HasRealThumbnail early-return so folders whose thumbnails
    // were pre-populated by PreloadCachedThumbnailsAsync still get their overlays.
    if (item.OverlayIcon is null && !item.FullPath.StartsWith("::", StringComparison.Ordinal))
      _overlayChannel.Writer.TryWrite(item);

    // Phase 1 — enqueue thumbnail upgrade if needed. Zero blocking work here.
    if (item.HasRealThumbnail)
      return;

    // Per-item folders (drives, virtual-path folders) must NOT go through the
    // ResizeToFit thumbnail path — they need IconOnly fetching.
    bool isPerItemFolder = IsPerItemFolder(item);
    bool canUpgrade = !IsIconOnlyMode(ViewMode) && !isPerItemFolder &&
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
    _overlayChannel = CreateOverlayChannel();

    var reader = _thumbChannel.Reader;
    var ct = _thumbCts.Token;
    for (var i = 0; i < ThumbConcurrency; i++)
      _ = ProcessThumbnailQueueAsync(reader, ct);

    var overlayReader = _overlayChannel.Reader;
    for (var i = 0; i < OverlayConcurrency; i++)
      _ = ProcessOverlayQueueAsync(overlayReader, ct);
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

        var ext = TypeIconKey(item);
        bool isPerItemFolder = IsPerItemFolder(item);
        // Per-item folders (drives, virtual-path) must use IconOnly — never ResizeToFit.
        bool canUpgrade = !isPerItemFolder && (item.IsFolder || _thumbnailExts.Contains(ext));
        bool isPerFile = _perFileIconExts.Contains(ext);

        try {
          if (!canUpgrade) {
            // ── Non-thumbnail items: just need a type/per-file icon ──────────
            byte[]? px = null;
            int w = 0, h = 0;
            if (isPerFile || isPerItemFolder || !_typeIconCache.ContainsKey((ext, size))) {
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
                // isPerFile (.exe etc.) icons are never cached; per-item folders
                // ARE cached but under their unique per-item key.
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

  // ── Overlay icon worker ───────────────────────────────────────────────────
  // Runs independently of the thumbnail pipeline.  Queries shell overlay index
  // via SHGetFileInfo, then extracts the overlay HICON from the system image list.
  // All COM/GDI work stays off the UI thread; only the final bitmap assignment
  // is dispatched at Low priority to avoid interfering with layout passes.

  private async Task ProcessOverlayQueueAsync(
      ChannelReader<ShellItem> reader, CancellationToken ct) {
    var dq = DispatcherQueue;
    try {
      await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false)) {
        if (ct.IsCancellationRequested) break;
        if (item.OverlayIcon is not null) continue;   // already loaded

        try {
          var (px, w, h, slot) = await NativeShell.GetOverlayIconPixelsAsync(item.FullPath, ct)
              .ConfigureAwait(false);
          if (ct.IsCancellationRequested) continue;
          if (px is null || slot == 0) continue;   // no overlay for this item

          // Materialise (or reuse) a WriteableBitmap for this overlay slot.
          // All files sharing the same shell extension handler get the same bitmap object.
          var capPx = px; var capW = w; var capH = h; var capSlot = slot;
          dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (ct.IsCancellationRequested || item.OverlayIcon is not null) return;
            WriteableBitmap? wb;
            lock (_overlayBitmapCache) {
              if (!_overlayBitmapCache.TryGetValue(capSlot, out wb)) {
                wb = NativeShell.PixelsToBitmapSync(capPx, capW, capH);
                _overlayBitmapCache[capSlot] = wb;
              }
            }
            if (wb is not null) item.OverlayIcon = wb;
          });
        } catch (OperationCanceledException) { break; } catch { }
      }
    } catch (OperationCanceledException) { }
  }

  // ── Double-tap navigation ─────────────────────────────────────────────────

  private void ShellView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) {
    var dep = e.OriginalSource as DependencyObject;
    while (dep is not null and not ListViewItem)
      dep = VisualTreeHelper.GetParent(dep);

    if (dep is not ListViewItem { Content: ShellItem item })
      return;

    if (item.IsFolder) {
      // Virtual-path items (Libraries children, This PC children, etc.) have parsing
      // paths like ::{GUID} or ::{GUID}\foo.library-ms that Directory.Exists rejects.
      if (item.FullPath.StartsWith("::", StringComparison.Ordinal)) {
        if (!string.IsNullOrEmpty(CurrentPath))
          _backStack.Push(CurrentPath);
        _forwardStack.Clear();
        LoadDirectory(item.FullPath);
      } else {
        Navigate(item.FullPath);
      }
    } else {
      try {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FullPath) {
          UseShellExecute = true
        });
      } catch { }
    }
  }

  // ── Context menu (right-tap) ──────────────────────────────────────────────

  private void OnShellViewRightTapped(object sender, RightTappedRoutedEventArgs e) {
    // Walk up to find the tapped ListViewItem, if any.
    var dep = e.OriginalSource as DependencyObject;
    while (dep is not null and not ListViewItem)
      dep = VisualTreeHelper.GetParent(dep);

    var hwnd  = GetOwnerHwnd();
    var point = e.GetPosition(DragSelectGrid);

    // Mirror real Explorer: Shift+right-click shows extended verbs.
    bool extendedVerbs = InputKeyboardSource
        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    if (dep is ListViewItem { Content: ShellItem tappedItem }) {
      // ── Item context menu ─────────────────────────────────────────────
      if (!ShellView.SelectedItems.Contains(tappedItem)) {
        ShellView.SelectedItems.Clear();
        ShellView.SelectedItems.Add(tappedItem);
      }

      var selected = ShellView.SelectedItems.OfType<ShellItem>().ToList();
      if (selected.Count > 0) {
        var paths = selected.Select(i => i.FullPath).ToList();
        _ = ShellContextMenuFlyout.ShowAsync(
            paths,
            CurrentPath,
            hwnd,
            point,
            DragSelectGrid,
            this,
            extendedVerbs);
      }
    } else {
      // ── Background (empty-space) context menu ─────────────────────────
      ShellView.SelectedItems.Clear();

      if (!string.IsNullOrEmpty(CurrentPath)) {
        _ = ShellContextMenuFlyout.ShowBackgroundAsync(
            CurrentPath,
            hwnd,
            point,
            DragSelectGrid,
            this,
            extendedVerbs);
      }
    }

    e.Handled = true;
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

  private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject {
    int count = VisualTreeHelper.GetChildrenCount(parent);
    for (int i = 0; i < count; i++) {
      var child = VisualTreeHelper.GetChild(parent, i);
      if (child is T match)
        yield return match;
      foreach (var nested in FindDescendants<T>(child))
        yield return nested;
    }
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
    // If a rename is in progress, a click anywhere outside the textbox should commit it.
    if (_renameActive)
      CommitRename();

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
      var ext = TypeIconKey(item);
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
    ApplyGrouping();
  }

  /// <summary>
  /// Rebuilds the ListView's ItemsSource to reflect the current <see cref="_groupColumn"/>.
  /// When grouping is active a <see cref="CollectionViewSource"/> wrapping <see cref="ShellItemGroup"/>
  /// objects is used; otherwise the list binds directly to the flat <see cref="Items"/> collection.
  /// </summary>
  private void ApplyGrouping() {
    if (string.IsNullOrEmpty(_groupColumn)) {
      // Restore flat binding if we were previously grouped.
      if (ShellView.ItemsSource != Items)
        ShellView.ItemsSource = Items;
      return;
    }

    var groups = Items
        .GroupBy(GetGroupLabel)
        .OrderBy(g => GetGroupSortOrder(g.Key))
        .Select(g => new ShellItemGroup(g.Key, g))
        .ToList();

    var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource {
      IsSourceGrouped = true,
      Source          = groups,
    };
    ShellView.ItemsSource = cvs.View;
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

  /// <summary>
  /// Sets the group-by column key (or null / empty string to remove grouping),
  /// rebuilds the grouped view, persists the change, and fires <see cref="GroupChanged"/>.
  /// </summary>
  public void ApplyGroupBy(string? column) {
    _groupColumn = string.IsNullOrEmpty(column) ? string.Empty : column;
    ApplySortToCollection();   // re-sort then re-group
    SaveCurrentFolderSettings();
    GroupChanged?.Invoke(this, EventArgs.Empty);
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
      // Apply group column (empty = no grouping).
      _groupColumn = settings.GroupColumn ?? string.Empty;
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
      GroupColumn   = _groupColumn,
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

  private void GroupHeader_Click(object sender, RoutedEventArgs e)
  {
    if (sender is Button { DataContext: ShellItemGroup group })
      group.Toggle();
  }

  // ── Inline rename ─────────────────────────────────────────────────────────

  private ShellItem? _renamingItem;
  private bool _renameActive;  // true while rename popup is open; guards against LostFocus re-entrance

  /// <summary>
  /// Queries the shell background menu for the current folder, extracts the "New" submenu,
  /// builds a <see cref="MenuFlyout"/> from its entries, and shows it attached to
  /// <paramref name="anchor"/>. Each item invokes the shell command and starts auto-rename.
  /// </summary>
  public async Task ShowNewMenuFlyoutAsync(Button anchor) {
    if (string.IsNullOrEmpty(CurrentPath)) return;

    var hwnd = GetOwnerHwnd();
    ShellContextMenuSession? session = null;
    try {
      session = await ShellContextMenuService.QueryBackgroundAsync(CurrentPath, hwnd);
    } catch (Exception ex) {
      System.Diagnostics.Debug.WriteLine($"[NewMenu] QueryBackgroundAsync failed: {ex}");
      return;
    }
    if (session is null) return;

    // Find the "New" top-level submenu.
    var newEntry = session.Items.FirstOrDefault(i =>
        string.Equals(i.Label, "New", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(i.Verb,  "NewFolder", StringComparison.OrdinalIgnoreCase));
    if (newEntry?.SubItems is not { Count: > 0 }) return;

    var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };

    foreach (var child in newEntry.SubItems) {
      if (child.IsSeparator) {
        flyout.Items.Add(new MenuFlyoutSeparator());
        continue;
      }
      if (string.IsNullOrWhiteSpace(child.Label)) continue;

      var mfi = new MenuFlyoutItem { Text = child.Label };
      // Attach icon from shell bitmap pixels if available.
      if (child.IconPixels is { Length: > 0 } px && child.IconW > 0 && child.IconH > 0) {
        try {
          var wb = NativeShell.PixelsToBitmapSync(px, child.IconW, child.IconH);
          if (wb is not null)
            mfi.Icon = new ImageIcon { Source = wb, Width = 16, Height = 16 };
        } catch { }
      }

      var capturedId  = child.Id;
      var capturedSes = session;
      var capturedDir = CurrentPath;
      mfi.Click += (_, _) => {
        BeginRenameOnNewItem(capturedDir);
        _ = Task.Run(async () => {
          try { await (capturedSes?.InvokeCommandAsync(capturedId, hwnd, capturedDir) ?? Task.CompletedTask); }
          catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NewMenu] InvokeCommand failed: {ex}"); }
        });
      };
      flyout.Items.Add(mfi);
    }

    flyout.Closed += (_, _) => { try { session?.Dispose(); } catch { } };
    flyout.ShowAt(anchor);
  }

  /// <summary>
  /// Call this immediately before invoking a "New" shell command for <paramref name="folder"/>.
  /// Snapshots the current items in that folder and begins polling for the first new item
  /// to appear, then auto-starts rename on it — exactly like real Explorer.
  /// </summary>
  public void BeginRenameOnNewItem(string folder) {
    if (string.IsNullOrEmpty(folder)) return;
    _renameOnNewItemFolder   = folder;
    _renameOnNewItemSnapshot = Items
        .Select(i => i.FullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    PollForNewItem();
  }

  private async void PollForNewItem() {
    var expectedFolder = _renameOnNewItemFolder;
    var snapshot       = _renameOnNewItemSnapshot;
    if (expectedFolder is null || snapshot is null) return;

    // Poll at 100 ms intervals for up to 5 seconds.
    for (int i = 0; i < 50; i++) {
      await Task.Delay(100);

      // Abort if user navigated away or a new poll was started.
      if (!string.Equals(_renameOnNewItemFolder, expectedFolder, StringComparison.OrdinalIgnoreCase))
        return;

      // Look for an item in the current list that was NOT in the snapshot.
      ShellItem? newItem = Items.FirstOrDefault(it =>
          !snapshot.Contains(it.FullPath) &&
          string.Equals(Path.GetDirectoryName(it.FullPath), expectedFolder,
              StringComparison.OrdinalIgnoreCase));

      if (newItem is not null) {
        _renameOnNewItemFolder   = null;
        _renameOnNewItemSnapshot = null;
        SelectItemAndBeginRename(newItem);
        return;
      }
    }

    // Timed out — give up silently.
    _renameOnNewItemFolder   = null;
    _renameOnNewItemSnapshot = null;
  }

  /// <summary>
  /// Selects <paramref name="item"/> and starts inline rename on it.
  /// Waits until the ListView container is fully realized before calling
  /// <see cref="BeginRename"/> so the rename box doesn't flash before the item renders.
  /// </summary>
  private async void SelectItemAndBeginRename(ShellItem item) {
    // Block the name-expansion popup until the item is fully rendered and rename starts.
    _suppressNameExpansion = true;
    CollapseAllNameExpansions();
    ShellView.SelectedItems.Clear();
    ShellView.SelectedItems.Add(item);
    ShellView.ScrollIntoView(item);

    // Poll until ContainerFromItem returns a non-null, fully laid-out container.
    // Each iteration yields one UI frame via a Low-priority enqueue, giving the
    // ListView virtualisation panel time to create and measure the container.
    for (int attempt = 0; attempt < 40; attempt++) {
      // Yield one UI frame at Low priority so layout/render can run.
      var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
          () => tcs.TrySetResult());
      await tcs.Task;

      var container = ShellView.ContainerFromItem(item) as ListViewItem;
      if (container is null) continue;

      // Ensure layout has been calculated (ActualHeight > 0 means a measure pass ran).
      if (container.ActualHeight <= 0) {
        container.UpdateLayout();
        if (container.ActualHeight <= 0) continue;
      }

      // Container is ready — wait one more tick for the render pass to paint it,
      // then start the rename so the box doesn't appear before the item is visible.
      await Task.Delay(800);
      _suppressNameExpansion = false;
      BeginRename();
      return;
    }

    // Fallback: give up waiting and just start rename anyway.
    _suppressNameExpansion = false;
    BeginRename();
  }

  /// <summary>
  /// Called by F2 key handler and can also be called from a context menu.
  /// </summary>
  public void BeginRename() {
    var item = ShellView.SelectedItems.OfType<ShellItem>().FirstOrDefault();
    if (item is null) return;

    var container = ShellView.ContainerFromItem(item) as ListViewItem;
    if (container is null) return;

    // Locate the name TextBlock before we hide anything.
    // Fall back to the first TextBlock in case the container was just recycled
    // and its text hasn't propagated yet.
    var nameBlock = FindDescendants<TextBlock>(container)
                      .FirstOrDefault(tb => tb.Text == item.Name)
                    ?? FindDescendants<TextBlock>(container).FirstOrDefault();
    if (nameBlock is null) return;

    _renamingItem = item;
    _renameActive = true;

    // Setting IsLabelHidden collapses the Canvas/TextBlock which invalidates
    // TransformToVisual, so measure everything while it is still visible.
    var nbTransform = nameBlock.TransformToVisual(DragSelectGrid);
    var nbPos = nbTransform.TransformPoint(new Windows.Foundation.Point(0, 0));

    // Derive the container top from the TextBlock position.
    // The name TextBlock is VerticalAlignment="Center" inside the row, so:
    //   containerTop = nbPos.Y - (container.ActualHeight - nameBlock.ActualHeight) / 2
    // This avoids trusting container.TransformToVisual which can lag behind the
    // panel's scroll position for newly inserted items, and avoids nameBlock.TransformToVisual(container)
    // which would include ContentPresenter internal offsets that are not purely centering.
    double containerTop = nbPos.Y - Math.Max(0, (container.ActualHeight - nameBlock.ActualHeight) / 2);
    var ctPos = new Windows.Foundation.Point(
        container.TransformToVisual(DragSelectGrid).TransformPoint(new Windows.Foundation.Point(0, 0)).X,
        containerTop);

    double tbLeft, tbTop, tbWidth, tbHeight;

    if (IsIconLabelExpandMode(_currentMode)) {
      // ── Icon modes (ExtraLarge / Large / Medium / Small) ────────────────────
      // Always go through the expansion popup so the TextBox gets reliable geometry.
      // For auto-rename the popup was suppressed up to this point, so open it now.
      if (!NameExpansionPopup.IsOpen || _expandedItem != item) {
        _suppressNameExpansion = false;
        UpdateNameExpansion();
      }

      // The popup was just opened (or was already open). Force a synchronous arrange
      // pass on the card so ActualHeight reflects the true rendered size, not just
      // the pre-arrange DesiredSize from the explicit Measure() call in UpdateNameExpansion.
      NameExpansionCard.UpdateLayout();

      // Overlay the TextBox exactly over the card using its now-accurate ActualHeight.
      tbLeft   = NameExpansionPopup.HorizontalOffset;
      tbTop    = NameExpansionPopup.VerticalOffset;
      tbWidth  = NameExpansionCard.Width;
      tbHeight = Math.Max(NameExpansionCard.ActualHeight, NameExpansionInBoundsHeight);
    } else {
      // ── Details / List / Tiles / Content ───────────────────────────────────
      tbWidth  = ComputeRenameTextBoxWidth(nameBlock);
      tbHeight = ComputeRenameTextBoxHeight(container, nameBlock);
      // Align left edge to the name TextBlock.
      tbLeft = nbPos.X - 4;
      // Top = container top + 1 px so the box sits inside the row borders on all sides.
      // ctPos.Y is the container's painted top in DragSelectGrid coords; since
      // tbHeight = container.ActualHeight - 2 the bottom edge lands at ctPos.Y + container.ActualHeight - 1.
      tbTop  = ctPos.Y + 1;
    }

    // Hide the original label now that we have all measurements.
    item.IsLabelHidden = true;
    // Also hide the expansion popup label so there is no overlapping text.
    NameExpansionText.Opacity = 0;

    // Match font to the actual label so the text renders identically.
    _renameTextBox.FontSize   = nameBlock.FontSize;
    _renameTextBox.FontWeight = nameBlock.FontWeight;

    // Match the text alignment and wrapping of the item template.
    bool isIconMode = IsIconLabelExpandMode(_currentMode);
    _renameTextBox.TextAlignment = isIconMode ? TextAlignment.Center : TextAlignment.Left;
    _renameTextBox.TextWrapping  = isIconMode ? TextWrapping.Wrap    : TextWrapping.NoWrap;

    _renameTextBox.Width  = tbWidth;
    _renameTextBox.Height = tbHeight;
    _renameTextBox.Text   = item.Name;

    _renamePopup.HorizontalOffset = tbLeft;
    _renamePopup.VerticalOffset   = tbTop;
    _renamePopup.IsOpen = true;

    _renameTextBox.Focus(FocusState.Programmatic);

    // Select just the stem (everything before the last dot) for files.
    if (!item.IsFolder) {
      var dot = item.Name.LastIndexOf('.');
      if (dot > 0) {
        _renameTextBox.SelectionStart  = 0;
        _renameTextBox.SelectionLength = dot;
      } else {
        _renameTextBox.SelectAll();
      }
    } else {
      _renameTextBox.SelectAll();
    }
  }

  private async void CommitRename() {
    if (_renamingItem is null) return;

    var item     = _renamingItem;
    var newName  = _renameTextBox.Text.Trim();
    _renamingItem = null;  // null immediately so any re-entrant LostFocus call is a no-op
    _renameActive = false;

    _renamePopup.IsOpen = false;
    // Restore the expansion label opacity right now — before any await — so the
    // card never shows as empty while the shell operation is in flight.
    NameExpansionText.Opacity = 1;

    item.IsLabelHidden = false;

    if (string.IsNullOrEmpty(newName) ||
        string.Equals(newName, item.Name, StringComparison.OrdinalIgnoreCase)) {
      UpdateNameExpansion();
      ShellView.Focus(FocusState.Programmatic);
      return;
    }

    // ── Optimistic update ────────────────────────────────────────────────────
    // Apply the new name to the UI immediately so the label changes without
    // waiting for IFileOperation to complete on the STA thread.
    var oldName     = item.Name;
    var oldFullPath = item.FullPath;
    var optimisticPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(item.FullPath) ?? string.Empty, newName);

    item.Name     = newName;
    item.FullPath = optimisticPath;
    UpdateNameExpansion();
    ShellView.Focus(FocusState.Programmatic);

    // ── Shell operation (background STA thread) ───────────────────────────────
    try {
      var hwnd    = GetOwnerHwnd();
      var newPath = await NativeShell.ShellRenameAsync(oldFullPath, newName, hwnd);

      if (newPath is not null) {
        // Shell may have adjusted the name (e.g. duplicate resolution).
        // Update to the authoritative final path/name if they differ.
        var finalName = System.IO.Path.GetFileName(newPath);
        if (!string.Equals(item.FullPath, newPath, StringComparison.OrdinalIgnoreCase)) {
          item.FullPath = newPath;
          item.Name     = finalName;
          UpdateNameExpansion();
        }
        ApplyGrouping();
      } else {
        // Operation failed — roll back to the original name.
        item.Name     = oldName;
        item.FullPath = oldFullPath;
        UpdateNameExpansion();
      }
    } catch {
      // Roll back on exception.
      item.Name     = oldName;
      item.FullPath = oldFullPath;
      UpdateNameExpansion();
    }
  }

  private void CancelRename() {
    var item = _renamingItem;
    _renamingItem = null;
    _renameActive = false;
    _renamePopup.IsOpen = false;
    if (item is not null) {
      item.IsLabelHidden = false;
      NameExpansionText.Opacity = 1;
      UpdateNameExpansion();
    }
    ShellView.Focus(FocusState.Programmatic);
  }

  /// <summary>
  /// Width for the rename TextBox in Details / List / Tiles / Content modes.
  /// Icon modes compute their own width directly in BeginRename.
  /// </summary>
  private double ComputeRenameTextBoxWidth(TextBlock nameBlock) {
    const double MinWidth = 80;
    return _currentMode switch {
      // Name column StackPanel width minus icon (16) minus spacing (6).
      ShellViewMode.Details => Math.Max(DetailsColumns.NameWidth - 22, MinWidth),
      // For List and Content use the actual measured TextBlock width.
      // TextTrimming means ActualWidth == the available name-column width, not content width.
      ShellViewMode.List    => Math.Max(nameBlock.ActualWidth + 8, MinWidth),
      ShellViewMode.Content => Math.Max(nameBlock.ActualWidth + 8, MinWidth),
      // Tiles name TextBlock has explicit Width="190".
      ShellViewMode.Tiles   => 190.0,
      _                     => Math.Max(nameBlock.ActualWidth + 8, MinWidth),
    };
  }

  /// <summary>
  /// Returns the height for the rename TextBox.
  /// By the time this is called the container is fully laid out by the panel,
  /// so container.ActualHeight is the authoritative row height.
  /// We subtract a small margin so the border stays inside the row.
  /// </summary>
  private double ComputeRenameTextBoxHeight(ListViewItem container, TextBlock nameBlock) {
    // container.ActualHeight is reliable here — SelectItemAndBeginRename already
    // waited for ActualHeight > 0 before calling BeginRename, and we never call
    // UpdateLayout() inside BeginRename so the panel's measured value is intact.
    double rowHeight = container.ActualHeight;
    if (rowHeight > 0)
      return Math.Max(rowHeight - 2, 20);   // 1 px top + 1 px bottom margin

    // Fallback (F2 on a container that somehow has no height yet): use text height.
    double labelHeight = nameBlock.ActualHeight;
    return Math.Max(labelHeight > 0 ? labelHeight + 4 : 24, 20);
  }

  private void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e) {
    if (e.Key == Windows.System.VirtualKey.Enter) {
      CommitRename();
      e.Handled = true;
    } else if (e.Key == Windows.System.VirtualKey.Escape) {
      CancelRename();
      e.Handled = true;
    }
  }

  private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e) {
    if (_renameActive)
      CommitRename();
  }


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

    if (e.Key == Windows.System.VirtualKey.F2) {
      BeginRename();
      e.Handled = true;
      return;
    }

    if (e.Key == Windows.System.VirtualKey.Delete) {
      var shift = Microsoft.UI.Input.InputKeyboardSource
          .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
      bool shiftDown = shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
      _ = DeleteSelectedAsync(permanent: shiftDown);
      e.Handled = true;
      return;
    }

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
        _ = PasteFromClipboardAsyncImpl();
        e.Handled = true;
        break;
      case Windows.System.VirtualKey.I:
        InvertSelection();
        e.Handled = true;
        break;
      case Windows.System.VirtualKey.D:
        SelectNone();
        e.Handled = true;
        break;
    }
  }

  /// <summary>True when this view has content on the clipboard (from Cut or Copy).</summary>
  public bool HasClipboardContent => _clipboardPaths is { Count: > 0 };

  /// <summary>Copies the selected items to the clipboard.</summary>
  public Task CopySelectedToClipboardAsync() => CopySelectedToClipboardAsync(cut: false);

  /// <summary>Cuts the selected items to the clipboard.</summary>
  public Task CutSelectedToClipboardAsync() => CopySelectedToClipboardAsync(cut: true);

  /// <summary>Pastes clipboard content into the current folder.</summary>
  public Task PasteFromClipboardAsync() => PasteFromClipboardAsyncImpl();

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
    ClipboardChanged?.Invoke(this, EventArgs.Empty);
  }

  private async Task PasteFromClipboardAsyncImpl() {
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
    ClipboardChanged?.Invoke(this, EventArgs.Empty);
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

  // ── Public context-menu actions ──────────────────────────────────────────

  /// <summary>
  /// Opens the selected items: folders are navigated in-app; files are launched via ShellExecute.
  /// </summary>
  public void OpenSelected() {
    foreach (var item in ShellView.SelectedItems.OfType<ShellItem>()) {
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
  }

  /// <summary>Selects all items in the current view (Ctrl+A).</summary>
  public void SelectAll() => ShellView.SelectAll();

  /// <summary>Clears the selection (Ctrl+D).</summary>
  public void SelectNone() => ShellView.SelectedItems.Clear();

  /// <summary>Inverts the current selection (Ctrl+I).</summary>
  public void InvertSelection() {
    var allItems  = Items.ToList();
    var currently = ShellView.SelectedItems.Cast<object>().ToHashSet();
    ShellView.SelectedItems.Clear();
    foreach (var item in allItems)
      if (!currently.Contains(item))
        ShellView.SelectedItems.Add(item);
  }

  /// <summary>
  /// Moves the selected items to the Recycle Bin using IFileOperation with shell UI.
  /// </summary>
  public async Task DeleteSelectedAsync(bool permanent = false) {
    var selected = ShellView.SelectedItems.OfType<ShellItem>().ToList();
    if (selected.Count == 0)
      return;

    var paths = selected.Select(i => i.FullPath).ToList();
    var hwnd  = GetOwnerHwnd();
    await NativeShell.ShellDeleteAsync(paths, hwnd, IsDarkMode(), permanent);

    // Remove items from view that were actually deleted.
    foreach (var path in paths) {
      if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) {
        var item = Items.FirstOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
          Items.Remove(item);
      }
    }
  }

  /// <summary>
  /// Shows the shell Properties dialog for the selected items.
  /// </summary>
  public void ShowPropertiesForSelected() {
    var hwnd = GetOwnerHwnd();
    foreach (var item in ShellView.SelectedItems.OfType<ShellItem>()) {
      try {
        NativeShell.ShowShellProperties(item.FullPath, hwnd);
      } catch { }
    }
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
      if (item.IsFolder && !existing.IsFolder) { Items.Insert(i, item); ApplyGrouping(); return; }
      if (!item.IsFolder && existing.IsFolder)
        continue;
      // Same tier: compare by sort key.
      int cmp = Comparer<object?>.Default.Compare(key(item), key(existing));
      if (_sortAscending ? cmp <= 0 : cmp >= 0) { Items.Insert(i, item); ApplyGrouping(); return; }
    }
    Items.Add(item);
    ApplyGrouping();
  }

  // ── Grouping helpers ─────────────────────────────────────────────────────

  private string GetGroupLabel(ShellItem item) =>
    _groupColumn switch {
      "Name" => GetNameGroupLabel(item.Name),
      "Date" => GetDateGroupLabel(item.DateModified),
      "Type" => string.IsNullOrWhiteSpace(item.ItemType) ? "Other" : item.ItemType,
      "Size" => GetSizeGroupLabel(item.SizeBytes, item.IsFolder),
      _      => GetNameGroupLabel(item.Name),
    };

  private static string GetNameGroupLabel(string name) {
    if (string.IsNullOrEmpty(name)) return "#";
    var c = char.ToUpperInvariant(name[0]);
    if (!char.IsLetter(c)) return "#";
    return c switch {
      >= 'A' and <= 'F' => "A \u2013 F",
      >= 'G' and <= 'L' => "G \u2013 L",
      >= 'M' and <= 'R' => "M \u2013 R",
      _                  => "S \u2013 Z",
    };
  }

  private static string GetDateGroupLabel(DateTime date) {
    if (date == default) return "Unspecified";
    var today = DateTime.Today;
    var diff  = (today - date.Date).Days;
    if (diff == 0)             return "Today";
    if (diff == 1)             return "Yesterday";
    if (diff <= today.DayOfWeek - DayOfWeek.Monday + 1)
                               return "Earlier this week";
    if (diff <= 14)            return "Last week";
    if (date.Month == today.Month && date.Year == today.Year)
                               return "Earlier this month";
    if (diff <= 60)            return "Last month";
    if (date.Year == today.Year)
                               return "Earlier this year";
    if (date.Year == today.Year - 1)
                               return "Last year";
    return "A long time ago";
  }

  private static readonly string[] _dateGroupOrder = [
    "Today", "Yesterday", "Earlier this week", "Last week",
    "Earlier this month", "Last month", "Earlier this year", "Last year",
    "A long time ago", "Unspecified"
  ];

  private static readonly string[] _sizeGroupOrder = [
    "Unspecified", "Tiny", "Small", "Medium", "Large", "Huge", "Gigantic"
  ];

  private static string GetSizeGroupLabel(long bytes, bool isFolder) {
    if (isFolder) return "Unspecified";
    return bytes switch {
      < 16_384L                => "Tiny",       // < 16 KB
      < 1_048_576L             => "Small",      // < 1 MB
      < 134_217_728L           => "Medium",     // < 128 MB
      < 1_073_741_824L         => "Large",      // < 1 GB
      < 4_294_967_296L         => "Huge",       // < 4 GB
      _                        => "Gigantic",
    };
  }

  private static readonly string[] _nameGroupOrder = [
    "#", "A \u2013 F", "G \u2013 L", "M \u2013 R", "S \u2013 Z",
  ];

  private int GetGroupSortOrder(string groupLabel) {
    var order = _groupColumn switch {
      "Date" => _dateGroupOrder,
      "Size" => _sizeGroupOrder,
      "Name" => _nameGroupOrder,
      _      => _nameGroupOrder,
    };
    var idx = Array.IndexOf(order, groupLabel);
    return idx < 0 ? int.MaxValue : idx;
  }

  private string GetColumnDisplayName(string key) =>
    key switch {
      "Name" => "Name",
      "Date" => "Date modified",
      "Type" => "Type",
      "Size" => "Size",
      _      => key,
    };
}

// ── ShellItemGroup ────────────────────────────────────────────────────────────

/// <summary>
/// A named, collapsible group of <see cref="ShellItem"/> objects for use with
/// <see cref="Microsoft.UI.Xaml.Data.CollectionViewSource"/>.
/// </summary>
public sealed class ShellItemGroup : ObservableCollection<ShellItem>, INotifyPropertyChanged
{
  private readonly List<ShellItem> _allItems;
  private bool _isExpanded = true;

  public string Key { get; }

  public bool IsExpanded {
    get => _isExpanded;
    set {
      if (_isExpanded == value) return;
      _isExpanded = value;
      OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsExpanded)));
      OnPropertyChanged(new PropertyChangedEventArgs(nameof(ChevronGlyph)));
      OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountText)));
      SyncItems();
    }
  }

  // Segoe Fluent / MDL2 chevrons
  public string ChevronGlyph => _isExpanded ? "\uE70D" : "\uE76C";

  public string CountText => _isExpanded ? string.Empty : $"({_allItems.Count})";

  public ShellItemGroup(string key, IEnumerable<ShellItem> items) : base()
  {
    Key       = key;
    _allItems = [.. items];
    foreach (var item in _allItems) Add(item);
  }

  public void Toggle() => IsExpanded = !_isExpanded;

  private void SyncItems()
  {
    if (_isExpanded) {
      foreach (var item in _allItems)
        if (!Contains(item)) Add(item);
    } else {
      ClearItems();
    }
  }
}
