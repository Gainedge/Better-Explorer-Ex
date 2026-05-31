using System;
using System.Diagnostics;
using System.IO;
using BetterExplorer.ShellApi;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;

namespace BetterExplorer.Controls;

/// <summary>
/// Composite shell browser control that bundles the navigation bar row,
/// the navigation tree, a resize splitter, and the file list view.
/// Drop this single control into any window or page to get a full-featured
/// file explorer pane.
/// </summary>
public sealed partial class ExplorerBrowser : UserControl {
  // ── Dependency Properties ─────────────────────────────────────────────────

  public static readonly DependencyProperty ViewModeProperty =
      DependencyProperty.Register(
          nameof(ViewMode), typeof(ShellViewMode), typeof(ExplorerBrowser),
          new PropertyMetadata(ShellViewMode.SmallIcons, OnViewModeChanged));

  public ShellViewMode ViewMode {
    get => (ShellViewMode)GetValue(ViewModeProperty);
    set => SetValue(ViewModeProperty, value);
  }

  private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
    if (d is ExplorerBrowser browser) {
      browser.FileList.ViewMode = (ShellViewMode)e.NewValue;
      browser.UpdateViewModeCheckmarks((ShellViewMode)e.NewValue);
    }
  }

  // ── Events ────────────────────────────────────────────────────────────────

  /// <summary>Forwarded from the inner <see cref="ShellListView.PathChanged"/>.</summary>
  public event EventHandler<string>? PathChanged;

  /// <summary>Forwarded from the inner <see cref="ShellListView.BusyChanged"/>.
  /// True while a navigation or search is in progress; false when complete.</summary>
  public event EventHandler<bool>? BusyChanged;

  /// <summary>Forwarded from the inner <see cref="ShellListView.SearchQueryChanged"/>.
  /// Non-null when a search is active; null when the search is cleared.</summary>
  public event EventHandler<string?>? SearchQueryChanged;

  // ── Search debounce ───────────────────────────────────────────────────────

  private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
  private string _pendingSearchText = string.Empty;
  private bool _applyingViewOptions;

  // ── Constructor ───────────────────────────────────────────────────────────

  public ExplorerBrowser() {
    InitializeComponent();
    FileList.PathChanged += OnPathChanged;
    FileList.SortChanged += OnSortChanged;
    FileList.GroupChanged += OnGroupChanged;
    FileList.SelectionChanged += (_, _) => UpdateToolbarButtonStates();
    FileList.ClipboardChanged += (_, _) => UpdateToolbarButtonStates();
    FileList.BusyChanged += (_, busy) => BusyChanged?.Invoke(this, busy);
    FileList.SearchQueryChanged += OnSearchQueryChanged;
    FileList.SearchQueryChanged += (_, q) => SearchQueryChanged?.Invoke(this, q);
    FileList.TreeDriveAdded += (_, root) => NavTreeView.NotifyDriveAdded(root);
    FileList.TreeDriveRemoved += (_, root) => NavTreeView.NotifyDriveRemoved(root);
    FileList.TreeFolderCreated += (_, path) => NavTreeView.NotifyFolderCreated(path);
    FileList.TreeFolderDeleted += (_, path) => NavTreeView.NotifyFolderDeleted(path);
    FileList.TreeFolderRenamed += (_, e) => NavTreeView.NotifyFolderRenamed(e.OldPath, e.NewPath);
    _searchDebounce.Tick += (_, _) => {
      _searchDebounce.Stop();
      if (string.IsNullOrEmpty(_pendingSearchText))
        FileList.ClearSearch();
      else
        FileList.SearchCurrentFolder(_pendingSearchText);
    };
    UpdateViewModeCheckmarks(FileList.ViewMode);
    UpdateSortCheckmarks(FileList.SortColumn, FileList.SortAscending);
    UpdateGroupCheckmarks(FileList.GroupColumn);
    Loaded += OnLoaded;
  }

  // Read browser-local setting, falling back to the General default if never set.
  private static bool ReadBrowserBool(string browserKey, string defaultKey, bool hardDefault)
  {
      var vals = ApplicationData.Current.LocalSettings.Values;
      if (vals.TryGetValue(browserKey, out var bv) && bv is bool bb) return bb;
      if (vals.TryGetValue(defaultKey,  out var dv) && dv is bool db) return db;
      return hardDefault;
  }

  private void OnLoaded(object sender, RoutedEventArgs e)
  {
      bool showHidden     = ReadBrowserBool(SettingsPage.BrowserShowHiddenFilesKey,
                                            SettingsPage.ShowHiddenFilesKey, false);
      bool showExtensions = ReadBrowserBool(SettingsPage.BrowserShowFileExtensionsKey,
                                            SettingsPage.ShowFileExtensionsKey, true);
      ApplyViewOptions(showHidden, showExtensions);
  }

  private void ApplyViewOptions(bool showHidden, bool showExtensions)
  {
      _applyingViewOptions = true;
      try {
          ShowHiddenFilesSwitch.IsOn    = showHidden;
          ShowFileExtensionsSwitch.IsOn = showExtensions;
      } finally {
          _applyingViewOptions = false;
      }
      FileList.ShowHiddenFiles      = showHidden;
      FileList.ShowFileExtensions   = showExtensions;
      NavTreeView.ShowHiddenFolders = showHidden;
  }

  // ── Public navigation API ─────────────────────────────────────────────────

  public void Navigate(string path) => FileList.Navigate(path);
  public void GoBack() => FileList.GoBack();
  public void GoForward() => FileList.GoForward();
  public string CurrentPath => FileList.CurrentPath;
  public bool CanGoBack => FileList.CanGoBack;
  public bool CanGoForward => FileList.CanGoForward;

  // ── Navigation bar callbacks ──────────────────────────────────────────────

  private void OnPathChanged(object? sender, string path) {
    // Kill any pending search debounce — the user navigated away before
    // the timer fired, so the old query must not be replayed in the new folder.
    _searchDebounce.Stop();
    _pendingSearchText = string.Empty;

    AddressBar.SetPath(path);
    BackButton.IsEnabled = FileList.CanGoBack;
    ForwardButton.IsEnabled = FileList.CanGoForward;
    var (fsParent, kfParent) = NativeShell.TryGetShellParent(path);
    UpLevelButton.IsEnabled = fsParent is not null || kfParent != Guid.Empty;
    RefreshButton.IsEnabled = true;
    SearchBox.IsEnabled = !path.StartsWith("::", StringComparison.Ordinal);
    // Clear the search text without triggering a new search — navigation is already done.
    SearchBox.TextChanged -= SearchBox_TextChanged;
    SearchBox.Text = string.Empty;
    SearchBox.TextChanged += SearchBox_TextChanged;
    NavTreeView.SyncToPath(path);
    UpdateViewModeCheckmarks(FileList.ViewMode);
    UpdateSortCheckmarks(FileList.SortColumn, FileList.SortAscending);
    UpdateGroupCheckmarks(FileList.GroupColumn);
    PathChanged?.Invoke(this, path);
    UpdateToolbarButtonStates();
    DriveToolsSection.Visibility =
        NativeShell.IsDriveRoot(path) ? Visibility.Visible : Visibility.Collapsed;
  }

  /// <summary>
  /// Called when the list view starts or clears a search.
  /// Updates the breadcrumb to show a search chip and keeps Back enabled.
  /// </summary>
  private void OnSearchQueryChanged(object? sender, string? query) {
    if (query is not null) {
      AddressBar.SetSearchMode(query);
      BackButton.IsEnabled = FileList.CanGoBack;
      ForwardButton.IsEnabled = FileList.CanGoForward;
    }
    // null means the search was cleared — OnPathChanged fires next and resets everything.
  }

  /// <summary>
  /// Called when the tab hosting this browser becomes the active tab.
  /// Forwards focus and name-expansion popup refresh to the inner list view.
  /// </summary>
  public void Activate() => FileList.NotifyActivated();
  public void Deactivate() => FileList.NotifyDeactivated();

  private void OnBreadcrumbPathRequested(object? sender, string path) {
    // Move focus directly onto the inner ListView so that any still-bubbling
    // keyboard events (Enter KeyUp from the breadcrumb AutoSuggestBox) are
    // absorbed by the list view instead of activating toolbar buttons such
    // as the ViewButton SplitButton (which would cycle the view mode).
    // Using FocusListView() also ensures selected items show the full accent
    // highlight (Selected state) rather than the dimmer SelectedUnfocused state.
    FileList.FocusListView();

    if (path.StartsWith("::{", StringComparison.Ordinal) && path.EndsWith("}")) {
      // Virtual folder path: ::{GUID-WITH-BRACES}
      // Format: ::{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
      // Extract: XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX (skip :: and keep {})
      var guidStr = path.Substring(2);  // :{GUID}
      if (Guid.TryParse(guidStr, out var guid)) {
        FileList.NavigateToKnownFolder(guid);
        return;
      }
    }
    FileList.Navigate(path);
  }

  private void OnTreeFolderSelected(object? sender, string path) =>
      FileList.Navigate(path);

  private void OnTreeKnownFolderSelected(object? sender, Guid folderId) =>
      FileList.NavigateToKnownFolder(folderId);

  private void BackButton_Click(object sender, RoutedEventArgs e) => FileList.GoBack();
  private void ForwardButton_Click(object sender, RoutedEventArgs e) => FileList.GoForward();
  private void UpLevelButton_Click(object sender, RoutedEventArgs e) => FileList.GoUp();
  private void RefreshButton_Click(object sender, RoutedEventArgs e) => FileList.Refresh();

  // ── Search box ────────────────────────────────────────────────────────────

  private void SearchBox_TextChanged(AutoSuggestBox sender,
      AutoSuggestBoxTextChangedEventArgs args) {
    if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) {
      _pendingSearchText = sender.Text;
      _searchDebounce.Stop();
      _searchDebounce.Start();
    }
  }

  private void SearchBox_QuerySubmitted(AutoSuggestBox sender,
      AutoSuggestBoxQuerySubmittedEventArgs args) {
    var query = args.QueryText ?? sender.Text;
    if (string.IsNullOrWhiteSpace(query))
      FileList.ClearSearch();
    else
      FileList.SearchCurrentFolder(query);
  }

  // ── New button ────────────────────────────────────────────────────────────

  private void NewButton_Click(object sender, RoutedEventArgs e) {
    _ = FileList.ShowNewMenuFlyoutAsync(NewButton);
  }

  // ── Shell primary toolbar buttons ─────────────────────────────────────────

  private void UpdateToolbarButtonStates() {
    bool hasSelection = FileList.HasSelection;
    bool isSingle = FileList.SelectionIsSingle;
    bool isFolder = FileList.SelectionIsSingleFolder;
    bool hasClip = FileList.HasClipboardContent;

    TbOpenButton.IsEnabled = hasSelection;
    ToolTipService.SetToolTip(TbOpenButton, isFolder ? "Open folder" : "Open");
    TbCutButton.IsEnabled = hasSelection;
    TbCopyButton.IsEnabled = hasSelection;
    TbPasteButton.IsEnabled = hasClip;
    TbRenameButton.IsEnabled = isSingle;
    TbDeleteButton.IsEnabled = hasSelection;
    TbPropertiesButton.IsEnabled = hasSelection;

    // Folder Tools contextual section
    FolderToolsSection.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
    if (isFolder) {
      var folderPath = FileList.SelectedFolderPath;
      TbRestoreFolderIconButton.IsEnabled =
          folderPath is not null &&
          NativeShell.HasCustomFolderIcon(folderPath);
    }

    // Picture Tools contextual section
    bool hasPicture = FileList.SelectionHasPicture;
    PictureToolsSection.Visibility = hasPicture ? Visibility.Visible : Visibility.Collapsed;
    if (hasPicture) {
      bool singlePicture = FileList.SelectedPicturePath is not null;
      TbRotateLeftButton.IsEnabled = singlePicture;
      TbRotateRightButton.IsEnabled = singlePicture;
      TbSetWallpaperButton.IsEnabled = singlePicture;
      TbEditWithButton.IsEnabled = singlePicture;
    }
  }

  private void TbOpenButton_Click(object sender, RoutedEventArgs e) => FileList.OpenSelected();
  private void TbCutButton_Click(object sender, RoutedEventArgs e) => _ = FileList.CutSelectedToClipboardAsync();
  private void TbCopyButton_Click(object sender, RoutedEventArgs e) => _ = FileList.CopySelectedToClipboardAsync();
  private void TbPasteButton_Click(object sender, RoutedEventArgs e) => _ = FileList.PasteFromClipboardAsync();
  private void TbRenameButton_Click(object sender, RoutedEventArgs e) => FileList.BeginRename();
  private void TbDeleteButton_Click(object sender, RoutedEventArgs e) {
    var shift = Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
    bool permanent = shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    _ = FileList.DeleteSelectedAsync(permanent);
  }
  private void TbPropertiesButton_Click(object sender, RoutedEventArgs e) => FileList.ShowPropertiesForSelected();

  private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e) => FileList.SelectAll();
  private void SelectNoneMenuItem_Click(object sender, RoutedEventArgs e) => FileList.SelectNone();
  private void InvertSelectionMenuItem_Click(object sender, RoutedEventArgs e) => FileList.InvertSelection();

  // ── View-mode switcher ────────────────────────────────────────────────────

  private static readonly ShellViewMode[] _viewCycle =
  [
      ShellViewMode.ExtraLargeIcons,
        ShellViewMode.LargeIcons,
        ShellViewMode.MediumIcons,
        ShellViewMode.SmallIcons,
        ShellViewMode.List,
        ShellViewMode.Details,
        ShellViewMode.Tiles,
        ShellViewMode.Content,
    ];

  private void ViewButton_Click(SplitButton sender, SplitButtonClickEventArgs e) {
    var current = FileList.ViewMode;
    var idx = Array.IndexOf(_viewCycle, current);
    var next = _viewCycle[(idx + 1) % _viewCycle.Length];
    FileList.ViewMode = next;
    UpdateViewModeCheckmarks(next);
  }

  private void ViewMenuItem_Click(object sender, RoutedEventArgs e) {
    if (sender is RadioButton { Tag: string tag }
        && Enum.TryParse<ShellViewMode>(tag, out var mode)) {
      FileList.ViewMode = mode;
      UpdateViewModeCheckmarks(mode);
      ViewButton.Flyout?.Hide();
    }
  }

  private void UpdateViewModeCheckmarks(ShellViewMode mode) {
    ViewMenuExtraLargeIcons.IsChecked = mode == ShellViewMode.ExtraLargeIcons;
    ViewMenuLargeIcons.IsChecked = mode == ShellViewMode.LargeIcons;
    ViewMenuMediumIcons.IsChecked = mode == ShellViewMode.MediumIcons;
    ViewMenuSmallIcons.IsChecked = mode == ShellViewMode.SmallIcons;
    ViewMenuList.IsChecked = mode == ShellViewMode.List;
    ViewMenuDetails.IsChecked = mode == ShellViewMode.Details;
    ViewMenuTiles.IsChecked = mode == ShellViewMode.Tiles;
    ViewMenuContent.IsChecked = mode == ShellViewMode.Content;
  }

  // ── Sort-mode switcher ──────────────────────────────────────────────

  private void ShowHiddenFilesSwitch_Toggled(object sender, RoutedEventArgs e) {
    if (_applyingViewOptions) return;
    bool show = ShowHiddenFilesSwitch.IsOn;
    if (FileList is not null)    FileList.ShowHiddenFiles      = show;
    if (NavTreeView is not null) NavTreeView.ShowHiddenFolders = show;
    ApplicationData.Current.LocalSettings.Values[SettingsPage.BrowserShowHiddenFilesKey] = show;
  }

  private void ShowFileExtensionsSwitch_Toggled(object sender, RoutedEventArgs e) {
    if (_applyingViewOptions) return;
    bool show = ShowFileExtensionsSwitch.IsOn;
    if (FileList is not null)    FileList.ShowFileExtensions   = show;
    ApplicationData.Current.LocalSettings.Values[SettingsPage.BrowserShowFileExtensionsKey] = show;
  }

  private void OnSortChanged(object? sender, EventArgs e) =>
      UpdateSortCheckmarks(FileList.SortColumn, FileList.SortAscending);

  private void UpdateSortCheckmarks(string column, bool ascending) {
    SortMenuName.IsChecked = column == "Name";
    SortMenuDate.IsChecked = column == "Date";
    SortMenuType.IsChecked = column == "Type";
    SortMenuSize.IsChecked = column == "Size";
    SortMenuAscending.IsChecked = ascending;
    SortMenuDescending.IsChecked = !ascending;
  }

  private void SortColumnMenuItem_Click(object sender, RoutedEventArgs e) {
    if (sender is RadioMenuFlyoutItem item && item.Tag is string col)
      FileList.ApplySort(col, FileList.SortAscending);
  }

  private void SortDirectionMenuItem_Click(object sender, RoutedEventArgs e) {
    if (sender is RadioMenuFlyoutItem item && item.Tag is string tag)
      FileList.ApplySort(FileList.SortColumn, tag == "Ascending");
  }

  private void OnGroupChanged(object? sender, EventArgs e) =>
      UpdateGroupCheckmarks(FileList.GroupColumn);

  private void GroupColumnMenuItem_Click(object sender, RoutedEventArgs e) {
    if (sender is RadioMenuFlyoutItem item) {
      var tag = item.Tag as string;
      FileList.ApplyGroupBy(string.IsNullOrEmpty(tag) ? null : tag);
    }
  }

  private void UpdateGroupCheckmarks(string groupColumn) {
    GroupMenuNone.IsChecked = string.IsNullOrEmpty(groupColumn);
    GroupMenuName.IsChecked = groupColumn == "Name";
    GroupMenuDate.IsChecked = groupColumn == "Date";
    GroupMenuType.IsChecked = groupColumn == "Type";
    GroupMenuSize.IsChecked = groupColumn == "Size";
  }

  // ── Folder Tools contextual toolbar handlers ──────────────────────────────

  private async void TbChangeFolderIcon_Click(object sender, RoutedEventArgs e) {
    var folderPath = FileList.SelectedFolderPath;
    if (folderPath is null)
      return;

    var picker = new FolderIconPickerDialog {
      XamlRoot = XamlRoot,
      RequestedTheme = ActualTheme,
    };
    // Lift the default max-width so the icon grid has enough room.
    picker.Resources["ContentDialogMaxWidth"] = 640.0;

    picker.StartWithDefaultFile();

    var result = await picker.ShowAsync();
    if (result != ContentDialogResult.Primary)
      return;

    if (picker.SelectedFile is { } iconFile) {
      NativeShell.SetFolderIcon(folderPath, iconFile, picker.SelectedIndex);
      FileList.RefreshItem(folderPath);
      UpdateToolbarButtonStates();
    }
  }

  private void TbRestoreFolderIcon_Click(object sender, RoutedEventArgs e) {
    var folderPath = FileList.SelectedFolderPath;
    if (folderPath is null)
      return;
    NativeShell.RestoreFolderIcon(folderPath);
    FileList.RefreshItemAfterIconClear(folderPath);
    UpdateToolbarButtonStates();
  }

  // ── Picture Tools contextual toolbar handlers ──────────────────────────────

  private async void TbRotateLeft_Click(object sender, RoutedEventArgs e) {
    var path = FileList.SelectedPicturePath;
    if (path is null)
      return;
    try {
      await NativeShell.RotateImageAsync(path, BitmapRotation.Clockwise270Degrees);
      FileList.RefreshItem(path);
    } catch { /* ignore transient IO errors */ }
  }

  private async void TbRotateRight_Click(object sender, RoutedEventArgs e) {
    var path = FileList.SelectedPicturePath;
    if (path is null)
      return;
    try {
      await NativeShell.RotateImageAsync(path, BitmapRotation.Clockwise90Degrees);
      FileList.RefreshItem(path);
    } catch { /* ignore transient IO errors */ }
  }

  private void TbSetWallpaper_Click(object sender, RoutedEventArgs e) {
    var path = FileList.SelectedPicturePath;
    if (path is null)
      return;
    NativeShell.SetWallpaper(path);
  }

  private void TbEditWithPaint_Click(object sender, RoutedEventArgs e) {
    var path = FileList.SelectedPicturePath;
    if (path is null)
      return;
    Process.Start(new ProcessStartInfo("mspaint.exe", $"\"{path}\"") { UseShellExecute = true });
  }

  private async void TbEditWithPhotos_Click(object sender, RoutedEventArgs e) {
    var path = FileList.SelectedPicturePath;
    if (path is null)
      return;
    try {
      var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
      await Launcher.LaunchFileAsync(file);
    } catch { /* app may not be installed */ }
  }

  private void TbEditWithPaint3D_Click(object sender, RoutedEventArgs e) {
    var path = FileList.SelectedPicturePath;
    if(path is null)
      return;
    Process.Start(new ProcessStartInfo("mspaint.exe",
        $"/canvas \"{path}\"") { UseShellExecute = true });
  }

  // ── Drive Tools contextual toolbar handlers ───────────────────────────────

  private void TbFormatDrive_Click(object sender, RoutedEventArgs e) {
    var path = FileList.CurrentPath;
    if (string.IsNullOrEmpty(path) || path.Length < 2)
      return;
    NativeShell.FormatDrive(path[0]);
  }

  private void TbDiskCleanup_Click(object sender, RoutedEventArgs e) {
    var path = FileList.CurrentPath;
    if (string.IsNullOrEmpty(path) || path.Length < 2)
      return;
    NativeShell.OpenDiskCleanup(path[0]);
  }

  private void TbDriveProperties_Click(object sender, RoutedEventArgs e) {
    var path = FileList.CurrentPath;
    if (!string.IsNullOrEmpty(path))
      NativeShell.ShowShellProperties(path);
  }

  // Drive Tools — scan the current drive / root path
  private async void TbFolderSize_Click(object sender, RoutedEventArgs e) {
    var path = FileList.CurrentPath;
    if (string.IsNullOrEmpty(path))
      return;
    var dlg = new FolderSizeDialog(path) { XamlRoot = XamlRoot };
    await dlg.ShowAsync();
  }

  // Folder Tools — scan the selected folder
  private async void TbFolderSizeFolder_Click(object sender, RoutedEventArgs e) {
    var path = FileList.SelectedFolderPath;
    if (string.IsNullOrEmpty(path))
      return;
    var dlg = new FolderSizeDialog(path) { XamlRoot = XamlRoot };
    await dlg.ShowAsync();
  }

  // ── Tree / file-list splitter ─────────────────────────────────────────────

  private bool _splitterDragging;
  private double _splitterStartX;
  private double _splitterStartWidth;

  private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e) {
    var el = (UIElement)sender;
    el.CapturePointer(e.Pointer);
    _splitterDragging = true;
    _splitterStartX = e.GetCurrentPoint(null).Position.X;
    _splitterStartWidth = ContentGrid.ColumnDefinitions[0].ActualWidth;
    e.Handled = true;
  }

  private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e) {
    if (!_splitterDragging)
      return;
    var col = ContentGrid.ColumnDefinitions[0];
    var x = e.GetCurrentPoint(null).Position.X;
    var delta = x - _splitterStartX;
    var newW = Math.Clamp(_splitterStartWidth + delta, col.MinWidth, col.MaxWidth);
    col.Width = new GridLength(newW);
    e.Handled = true;
  }

  private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e) {
    if (!_splitterDragging)
      return;
    _splitterDragging = false;
    ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    e.Handled = true;
  }

  private ContentDialog? _settingsDialog;

  private async void OnSettingsButtonClick(object sender, RoutedEventArgs e) {
    // Only one instance at a time.
    if (_settingsDialog is not null)
      return;

    var page = new SettingsPage();

    _settingsDialog = new ContentDialog {
      Title = "Settings",
      Content = page,
      XamlRoot = XamlRoot,
      RequestedTheme = ActualTheme,
    };
    // Lift the default MaxWidth cap so the dialog grows to fit SettingsPage.
    _settingsDialog.Resources["ContentDialogMaxWidth"] = 1024.0;

    page.CloseRequested += OnSettingsCloseRequested;
    SettingsPage.ThemeChangeRequested += OnSettingsThemeChanged;

    await _settingsDialog.ShowAsync();

    SettingsPage.ThemeChangeRequested -= OnSettingsThemeChanged;
    page.CloseRequested -= OnSettingsCloseRequested;
    _settingsDialog = null;
  }

  private void OnSettingsThemeChanged(Microsoft.UI.Xaml.ElementTheme theme) {
    if (_settingsDialog is not null)
      _settingsDialog.RequestedTheme = theme;
  }

  private void OnSettingsCloseRequested(object? sender, EventArgs e)
      => _settingsDialog?.Hide();

}
