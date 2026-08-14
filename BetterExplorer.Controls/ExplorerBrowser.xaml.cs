using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

  /// <summary>
  /// Forwarded from the inner <see cref="ShellListView.NavigationCompleted"/>. Fires
  /// once a navigation has truly finished — items, selection, status bar, folder
  /// watcher AND background icon/thumbnail warming are all done — unlike
  /// <see cref="PathChanged"/> which fires as soon as items are visible.
  /// </summary>
  public event EventHandler<string>? NavigationCompleted;

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
  private bool _searchGlobal;
  private bool _searchMatchCase;
  private bool _searchWholeWord;
  private bool _searchRegex;
  private bool _searchContents;
  private string _searchSizeFilter = "";
  private string _searchDateFilter = "";
  private string _searchTypeFilter = "";
  // Set by the breadcrumb bar commit path; consumed in OnPathChanged to move
  // focus to the list view AFTER SearchBox.IsEnabled has been updated (which
  // otherwise steals focus from any earlier programmatic Focus() call).
  private bool _focusListViewAfterNav;

  // ── Constructor ───────────────────────────────────────────────────────────

  public ExplorerBrowser() {
    InitializeComponent();
    FileList.PathChanged += OnPathChanged;
    FileList.NavigationCompleted += (_, path) => NavigationCompleted?.Invoke(this, path);
    FileList.SortChanged += OnSortChanged;
    FileList.GroupChanged += OnGroupChanged;
    FileList.SelectionChanged += (_, _) => UpdateToolbarButtonStates();
    FileList.ClipboardChanged += (_, _) => UpdateToolbarButtonStates();
    FileList.BusyChanged += (_, busy) => {
      BusyChanged?.Invoke(this, busy);
      if (!busy && SearchToolsSection.Visibility == Visibility.Visible)
        SearchSaveResultsButton.IsEnabled = FileList.Items.Count > 0;
    };
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
        RunSearch(_pendingSearchText);
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
  public void ArmPendingNavigation(string path) => FileList.ArmPendingNavigation(path);
  public void GoBack() => FileList.GoBack();
  public void GoForward() => FileList.GoForward();
  public string CurrentPath => FileList.CurrentPath;
  public bool CanGoBack => FileList.CanGoBack;
  public bool CanGoForward => FileList.CanGoForward;

  /// <summary>
  /// Forces an immediate, synchronous layout pass on this control and the inner
  /// <see cref="ShellListView"/> (<c>UpdateLayout</c> runs measure/arrange right away
  /// instead of waiting for the next XAML layout cycle). Used right after this control
  /// is first attached to the live visual tree (e.g. becoming the selected TabViewItem's
  /// content) so its content is actually realized deterministically instead of relying
  /// on guessing how many composition frames a lazy layout pass will need.
  /// </summary>
  public void ForceLayout()
  {
    UpdateLayout();
    FileList.UpdateLayout();
  }

  // ── Navigation bar callbacks ──────────────────────────────────────────────

  private void OnPathChanged(object? sender, string path) {
    // Kill any pending search debounce — the user navigated away before
    // the timer fired, so the old query must not be replayed in the new folder.
    _searchDebounce.Stop();
    _pendingSearchText = string.Empty;

    AddressBar.SetPath(path);
    BackButton.IsEnabled = FileList.CanGoBack;
    ForwardButton.IsEnabled = FileList.CanGoForward;
    var (fsParent, kfParent) = FileList.IsFtpMode ? (null, Guid.Empty) : NativeShell.TryGetShellParent(path);
    UpLevelButton.IsEnabled = fsParent is not null || kfParent != Guid.Empty;
    RefreshButton.IsEnabled = true;
    SearchBox.IsEnabled = !path.StartsWith("::", StringComparison.Ordinal);
    // Clear the search text without triggering a new search — navigation is already done.
    SearchBox.TextChanged -= SearchBox_TextChanged;
    SearchBox.Text = string.Empty;
    SearchBox.TextChanged += SearchBox_TextChanged;
    // Reset search toolbar toggles and hide the toolbar.
    _searchGlobal = false;
    _searchMatchCase = false;
    _searchWholeWord = false;
    _searchRegex = false;
    _searchContents = false;
    _searchSizeFilter = "";
    _searchDateFilter = "";
    _searchTypeFilter = "";
    SearchGlobalButton.IsChecked = false;
    SearchMatchCaseButton.IsChecked = false;
    SearchWholeWordButton.IsChecked = false;
    SearchRegexButton.IsChecked = false;
    SearchContentsButton.IsChecked = false;
    SearchSaveResultsButton.IsEnabled = false;
    SearchSizeLabel.Text = "Size";
    SearchDateLabel.Text = "Date";
    SearchTypeLabel.Text = "Type";
    HideSearchToolbar();
    NavTreeView.SyncToPath(path);
    UpdateViewModeCheckmarks(FileList.ViewMode);
    UpdateSortCheckmarks(FileList.SortColumn, FileList.SortAscending);
    UpdateGroupCheckmarks(FileList.GroupColumn);
    PathChanged?.Invoke(this, path);
    UpdateToolbarButtonStates();
    bool isDriveRoot = NativeShell.IsDriveRoot(path);
    bool isThisPC = IsThisPCPath(path);
    DriveToolsSection.Visibility = isDriveRoot ? Visibility.Visible : Visibility.Collapsed;
    ThisPCToolsSection.Visibility = isThisPC ? Visibility.Visible : Visibility.Collapsed;
    NewButton.IsEnabled = !isThisPC;

    if (_focusListViewAfterNav) {
      _focusListViewAfterNav = false;
      FileList.FocusListView();
    }
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
      ShowSearchToolbar();
    } else {
      HideSearchToolbar();
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

  private void OnBreadcrumbListViewFocusRequested(object? sender, EventArgs e) {
    // Don't call FocusListView() here — OnPathChanged (fired by the navigation
    // that was just committed) runs AFTER this event and contains
    // SearchBox.IsEnabled which steals focus. Set the flag instead so
    // OnPathChanged claims focus as its very last action.
    _focusListViewAfterNav = true;
  }

  private void OnTreeFolderSelected(object? sender, string path)
  {
      FileList.Navigate(path);
  }

  private void OnTreeKnownFolderSelected(object? sender, Guid folderId)
  {
      FileList.NavigateToKnownFolder(folderId);
  }

  private async void OnTreeFtpSiteSelected(object? sender, FtpSiteEntry site)
  {
      await FileList.NavigateToFtpSiteAsync(site);
  }

  // ── GetFtpSubMenu kept only to support sort/group sub-menu index lookup ──

  private MenuFlyoutSubItem? GetFtpSubMenu(int index)
  {
      int found = 0;
      foreach (var item in SortFlyout.Items)
          if (item is MenuFlyoutSubItem sub)
              if (found++ == index) return sub;
      return null;
  }

  private void BackButton_Click(object sender, RoutedEventArgs e) => FileList.GoBack();
  private void ForwardButton_Click(object sender, RoutedEventArgs e) => FileList.GoForward();
  private void UpLevelButton_Click(object sender, RoutedEventArgs e) => FileList.GoUp();
  private void RefreshButton_Click(object sender, RoutedEventArgs e) => FileList.Refresh();

  private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
  {
    FileList.Refresh();
    args.Handled = true;
  }

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
      RunSearch(query);
  }

  // ── Search toolbar ───────────────────────────────────────────────────────

  /// <summary>
  /// Builds a query string with modifier prefixes based on the current toggle
  /// states, then dispatches to either global or folder-scoped search.
  /// </summary>
  private void RunSearch(string rawQuery) {
    string query = BuildSearchQuery(rawQuery);
    if (_searchGlobal)
      FileList.SearchGlobal(query);
    else
      FileList.SearchCurrentFolder(query);
  }

  /// <summary>
  /// Prepends modifier prefixes to the raw query based on toggle and filter
  /// states.  Everything supports inline operators (case:, content:, size:,
  /// datemodified:, ext:, wholeword:, regex:).  Windows Search uses AQS
  /// properties (System.FileName:, System.FileContents:, System.Size:,
  /// System.DateModified:, System.FileExtension:).
  /// </summary>
  private string BuildSearchQuery(string rawQuery) {
    bool useEverything = SettingsPage.SearchEngine == "Everything"
                         && EverythingSearch.IsAvailable();
    var parts = new System.Collections.Generic.List<string>();

    if (_searchMatchCase)
      parts.Add(useEverything ? "case:" : "System.FileName:");
    if (_searchWholeWord)
      parts.Add(useEverything ? "wholeword:" : "\"");
    if (_searchRegex && useEverything)
      parts.Add("regex:");
    if (_searchContents)
      parts.Add(useEverything ? "content:" : "System.FileContents:");

    // Size filter
    if (!string.IsNullOrEmpty(_searchSizeFilter)) {
      parts.Add(useEverything ? BuildEverythingSize(_searchSizeFilter) : BuildWinSearchSize(_searchSizeFilter));
    }

    // Date modified filter
    if (!string.IsNullOrEmpty(_searchDateFilter)) {
      parts.Add(useEverything ? BuildEverythingDate(_searchDateFilter) : BuildWinSearchDate(_searchDateFilter));
    }

    // File type filter
    if (!string.IsNullOrEmpty(_searchTypeFilter)) {
      parts.Add(useEverything ? BuildEverythingType(_searchTypeFilter) : BuildWinSearchType(_searchTypeFilter));
    }

    parts.Add(_searchWholeWord && !useEverything ? rawQuery + "\"" : rawQuery);

    return string.Concat(parts);
  }

  // ── Everything query builders ────────────────────────────────────────────

  private static string BuildEverythingSize(string tag) => tag switch {
    "tiny"      => "size:<10kb ",
    "small"     => "size:10kb-100kb ",
    "medium"    => "size:100kb-1mb ",
    "large"     => "size:1mb-16mb ",
    "huge"      => "size:16mb-128mb ",
    "gigantic"  => "size:>128mb ",
    _ => ""
  };

  private static string BuildEverythingDate(string tag) => tag switch {
    "today"     => "datemodified:today ",
    "yesterday" => "datemodified:yesterday ",
    "thisweek"  => "datemodified:thisweek ",
    "lastweek"  => "datemodified:lastweek ",
    "thismonth" => "datemodified:thismonth ",
    "lastmonth" => "datemodified:lastmonth ",
    "thisyear"  => "datemodified:thisyear ",
    "lastyear"  => "datemodified:lastyear ",
    _ => ""
  };

  private static string BuildEverythingType(string tag) => tag switch {
    "folder"   => "folder: ",
    "document" => "ext:doc docx pdf txt rtf odt ",
    "picture"  => "ext:jpg jpeg png gif bmp tiff webp svg ",
    "video"    => "ext:mp4 avi mkv mov wmv flv webm ",
    "music"    => "ext:mp3 wav flac aac ogg m4a wma ",
    "archive"  => "ext:zip rar 7z tar gz ",
    "exe"      => "ext:exe msi bat cmd ps1 ",
    _ => ""
  };

  // ── Windows Search AQS query builders ───────────────────────────────────

  private static string BuildWinSearchSize(string tag) => tag switch {
    "tiny"      => "System.Size:<10240 ",
    "small"     => "System.Size:10240-102400 ",
    "medium"    => "System.Size:102400-1048576 ",
    "large"     => "System.Size:1048576-16777216 ",
    "huge"      => "System.Size:16777216-134217728 ",
    "gigantic"  => "System.Size:>134217728 ",
    _ => ""
  };

  private static string BuildWinSearchDate(string tag) => tag switch {
    "today"     => "System.DateModified:today ",
    "yesterday" => "System.DateModified:yesterday ",
    "thisweek"  => "System.DateModified:thisweek ",
    "lastweek"  => "System.DateModified:lastweek ",
    "thismonth" => "System.DateModified:thismonth ",
    "lastmonth" => "System.DateModified:lastmonth ",
    "thisyear"  => "System.DateModified:thisyear ",
    "lastyear"  => "System.DateModified:lastyear ",
    _ => ""
  };

  private static string BuildWinSearchType(string tag) => tag switch {
    "folder"   => "System.Kind:folder ",
    "document" => "System.Kind:document ",
    "picture"  => "System.Kind:picture ",
    "video"    => "System.Kind:video ",
    "music"    => "System.Kind:music ",
    "archive"  => "ext:zip rar 7z tar gz ",
    "exe"      => "ext:exe msi bat cmd ps1 ",
    _ => ""
  };

  // ── Search toolbar visibility ────────────────────────────────────────────

  private void ShowSearchToolbar() {
    SearchToolsSection.Visibility = Visibility.Visible;
    SearchSaveResultsButton.IsEnabled = FileList.Items.Count > 0;
  }

  private void HideSearchToolbar() {
    SearchToolsSection.Visibility = Visibility.Collapsed;
  }

  // ── Search toolbar handlers ──────────────────────────────────────────────

  private void SearchGlobalButton_Click(object sender, RoutedEventArgs e) {
    _searchGlobal = SearchGlobalButton.IsChecked == true;
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchMatchCaseButton_Click(object sender, RoutedEventArgs e) {
    _searchMatchCase = SearchMatchCaseButton.IsChecked == true;
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchWholeWordButton_Click(object sender, RoutedEventArgs e) {
    _searchWholeWord = SearchWholeWordButton.IsChecked == true;
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchRegexButton_Click(object sender, RoutedEventArgs e) {
    _searchRegex = SearchRegexButton.IsChecked == true;
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchContentsButton_Click(object sender, RoutedEventArgs e) {
    _searchContents = SearchContentsButton.IsChecked == true;
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchSizeMenuItem_Click(object sender, RoutedEventArgs e) {
    if (sender is RadioMenuFlyoutItem item) {
      _searchSizeFilter = item.Tag as string ?? "";
      SearchSizeLabel.Text = string.IsNullOrEmpty(_searchSizeFilter) ? "Size" : item.Text;
    }
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchDateMenuItem_Click(object sender, RoutedEventArgs e) {
    if (sender is RadioMenuFlyoutItem item) {
      _searchDateFilter = item.Tag as string ?? "";
      SearchDateLabel.Text = string.IsNullOrEmpty(_searchDateFilter) ? "Date" : item.Text;
    }
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchTypeMenuItem_Click(object sender, RoutedEventArgs e) {
    if (sender is RadioMenuFlyoutItem item) {
      _searchTypeFilter = item.Tag as string ?? "";
      SearchTypeLabel.Text = string.IsNullOrEmpty(_searchTypeFilter) ? "Type" : item.Text;
    }
    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
      RunSearch(SearchBox.Text);
  }

  private void SearchSaveResultsButton_Click(object sender, RoutedEventArgs e) {
    _ = SearchSaveResultsAsync();
  }

  private async Task SearchSaveResultsAsync() {
    if (FileList.Items.Count == 0) return;
    var picker = new Windows.Storage.Pickers.FileSavePicker();
    picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
    picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
    picker.SuggestedFileName = "SearchResults";
    try {
      if (SettingsPage.GetMainWindowHandle is { } getter)
        WinRT.Interop.InitializeWithWindow.Initialize(picker, getter());
    } catch { }
    var file = await picker.PickSaveFileAsync();
    if (file is null) return;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Name,Path,Type,Size,Date Modified");
    foreach (var item in FileList.Items) {
      sb.AppendLine($"\"{item.Name}\",\"{item.FullPath}\",\"{item.ItemType}\",\"{item.Size}\",\"{item.DateModified}\"");
    }
    await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
  }

  private void CloseSearchButton_Click(object sender, RoutedEventArgs e) {
    FileList.ClearSearch();
    // Clear the search box text without triggering a new search.
    SearchBox.TextChanged -= SearchBox_TextChanged;
    SearchBox.Text = string.Empty;
    SearchBox.TextChanged += SearchBox_TextChanged;
    _searchGlobal = false;
    _searchMatchCase = false;
    _searchWholeWord = false;
    _searchRegex = false;
    _searchContents = false;
    _searchSizeFilter = "";
    _searchDateFilter = "";
    _searchTypeFilter = "";
    SearchGlobalButton.IsChecked = false;
    SearchMatchCaseButton.IsChecked = false;
    SearchWholeWordButton.IsChecked = false;
    SearchRegexButton.IsChecked = false;
    SearchContentsButton.IsChecked = false;
    SearchSaveResultsButton.IsEnabled = false;
    SearchSizeLabel.Text = "Size";
    SearchDateLabel.Text = "Date";
    SearchTypeLabel.Text = "Type";
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
    // Share button logic is set later based on selected item attributes.
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

    // Share button: exclude hidden items, allow folders only if they are archives,
    // and for shortcuts only allow them if they are link items (junctions/symlinks).
    var selectedItem = FileList.FirstSelectedItem;
    TbShareButton.IsEnabled = isSingle &&
        selectedItem is not null &&
        !selectedItem.IsHidden &&
        (!selectedItem.IsShortcut || selectedItem.IsLinkItem) &&
        (!selectedItem.IsFolder || selectedItem.IsArchive);

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
  private void TbShareButton_Click(object sender, RoutedEventArgs e) { _ = FileList.ShareSelectedAsync(); }
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
    var i2 = Array.IndexOf(_viewCycle, current);
    var n2 = _viewCycle[(i2 + 1) % _viewCycle.Length];
    FileList.ViewMode = n2;
    UpdateViewModeCheckmarks(n2);
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

  // ── This PC contextual toolbar handlers ────────────────────────────────

  /// <summary>Returns true when <paramref name="path"/> is the This PC virtual folder.</summary>
  private static bool IsThisPCPath(string path) =>
      path.Contains(NativeShell.FOLDERID_ComputerFolder.ToString(),
                    StringComparison.OrdinalIgnoreCase);

  private void TbAddNetworkLocation_Click(object sender, RoutedEventArgs e) =>
      NativeShell.ShowAddNetworkLocationWizard();

  private void TbMapNetworkDrive_Click(object sender, RoutedEventArgs e) =>
      NativeShell.ShowMapNetworkDriveDialog();

  private void TbDisconnectNetworkDrive_Click(object sender, RoutedEventArgs e) =>
      NativeShell.ShowDisconnectNetworkDriveDialog();

  private void TbConnectMediaServer_Click(object sender, RoutedEventArgs e) =>
      NativeShell.ShowMediaServerConnectionDialog();

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
