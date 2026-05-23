using System;
using BetterExplorer.ShellApi;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BetterExplorer.Controls;

/// <summary>
/// Composite shell browser control that bundles the navigation bar row,
/// the navigation tree, a resize splitter, and the file list view.
/// Drop this single control into any window or page to get a full-featured
/// file explorer pane.
/// </summary>
public sealed partial class ExplorerBrowser : UserControl
{
    // ── Dependency Properties ─────────────────────────────────────────────────

    public static readonly DependencyProperty ViewModeProperty =
        DependencyProperty.Register(
            nameof(ViewMode), typeof(ShellViewMode), typeof(ExplorerBrowser),
            new PropertyMetadata(ShellViewMode.SmallIcons, OnViewModeChanged));

    public ShellViewMode ViewMode
    {
        get => (ShellViewMode)GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ExplorerBrowser browser)
        {
            browser.FileList.ViewMode = (ShellViewMode)e.NewValue;
            browser.UpdateViewModeCheckmarks((ShellViewMode)e.NewValue);
        }
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Forwarded from the inner <see cref="ShellListView.PathChanged"/>.</summary>
    public event EventHandler<string>? PathChanged;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ExplorerBrowser()
    {
        InitializeComponent();
        FileList.PathChanged  += OnPathChanged;
        FileList.SortChanged  += OnSortChanged;
        UpdateViewModeCheckmarks(FileList.ViewMode);
        UpdateSortCheckmarks(FileList.SortColumn, FileList.SortAscending);
    }

    // ── Public navigation API ─────────────────────────────────────────────────

    public void Navigate(string path)       => FileList.Navigate(path);
    public void GoBack()                    => FileList.GoBack();
    public void GoForward()                 => FileList.GoForward();
    public string CurrentPath               => FileList.CurrentPath;
    public bool CanGoBack                   => FileList.CanGoBack;
    public bool CanGoForward                => FileList.CanGoForward;

    // ── Navigation bar callbacks ──────────────────────────────────────────────

    private void OnPathChanged(object? sender, string path)
    {
        AddressBar.SetPath(path);
        BackButton.IsEnabled    = FileList.CanGoBack;
        ForwardButton.IsEnabled = FileList.CanGoForward;
        var (fsParent, kfParent) = NativeShell.TryGetShellParent(path);
        UpLevelButton.IsEnabled = fsParent is not null || kfParent != Guid.Empty;
        RefreshButton.IsEnabled = true;
        NavTreeView.SyncToPath(path);
        UpdateViewModeCheckmarks(FileList.ViewMode);
        UpdateSortCheckmarks(FileList.SortColumn, FileList.SortAscending);
        PathChanged?.Invoke(this, path);
    }

    private void OnBreadcrumbPathRequested(object? sender, string path)
    {
        // Move focus directly onto the inner ListView so that any still-bubbling
        // keyboard events (Enter KeyUp from the breadcrumb AutoSuggestBox) are
        // absorbed by the list view instead of activating toolbar buttons such
        // as the ViewButton SplitButton (which would cycle the view mode).
        // Using FocusListView() also ensures selected items show the full accent
        // highlight (Selected state) rather than the dimmer SelectedUnfocused state.
        FileList.FocusListView();

        if (path.StartsWith("::{", StringComparison.Ordinal) && path.EndsWith("}"))
        {
            // Virtual folder path: ::{GUID-WITH-BRACES}
            // Format: ::{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
            // Extract: XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX (skip :: and keep {})
            var guidStr = path.Substring(2);  // :{GUID}
            if (Guid.TryParse(guidStr, out var guid))
            {
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

    private void BackButton_Click(object sender, RoutedEventArgs e)       => FileList.GoBack();
    private void ForwardButton_Click(object sender, RoutedEventArgs e)    => FileList.GoForward();
    private void UpLevelButton_Click(object sender, RoutedEventArgs e)    => FileList.GoUp();
    private void RefreshButton_Click(object sender, RoutedEventArgs e)    => FileList.Refresh();

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

    private void ViewButton_Click(SplitButton sender, SplitButtonClickEventArgs e)
    {
        var current = FileList.ViewMode;
        var idx = Array.IndexOf(_viewCycle, current);
        var next = _viewCycle[(idx + 1) % _viewCycle.Length];
        FileList.ViewMode = next;
        UpdateViewModeCheckmarks(next);
    }

    private void ViewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }
            && Enum.TryParse<ShellViewMode>(tag, out var mode))
        {
            FileList.ViewMode = mode;
            UpdateViewModeCheckmarks(mode);
            ViewButton.Flyout?.Hide();
        }
    }

    private void UpdateViewModeCheckmarks(ShellViewMode mode)
    {
        ViewMenuExtraLargeIcons.IsChecked = mode == ShellViewMode.ExtraLargeIcons;
        ViewMenuLargeIcons.IsChecked      = mode == ShellViewMode.LargeIcons;
        ViewMenuMediumIcons.IsChecked     = mode == ShellViewMode.MediumIcons;
        ViewMenuSmallIcons.IsChecked      = mode == ShellViewMode.SmallIcons;
        ViewMenuList.IsChecked            = mode == ShellViewMode.List;
        ViewMenuDetails.IsChecked         = mode == ShellViewMode.Details;
        ViewMenuTiles.IsChecked           = mode == ShellViewMode.Tiles;
        ViewMenuContent.IsChecked         = mode == ShellViewMode.Content;
    }

    // ── Sort-mode switcher ──────────────────────────────────────────────

    private void OnSortChanged(object? sender, EventArgs e) =>
        UpdateSortCheckmarks(FileList.SortColumn, FileList.SortAscending);

    private void UpdateSortCheckmarks(string column, bool ascending)
    {
        SortMenuName.IsChecked       = column == "Name";
        SortMenuDate.IsChecked       = column == "Date";
        SortMenuType.IsChecked       = column == "Type";
        SortMenuSize.IsChecked       = column == "Size";
        SortMenuAscending.IsChecked  = ascending;
        SortMenuDescending.IsChecked = !ascending;
    }

    private void SortColumnMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string col })
        {
            FileList.ApplySort(col, FileList.SortAscending);
            SortFlyout.Hide();
        }
    }

    private void SortDirectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            FileList.ApplySort(FileList.SortColumn, tag == "Ascending");
            SortFlyout.Hide();
        }
    }

    // ── Tree / file-list splitter ─────────────────────────────────────────────

    private bool   _splitterDragging;
    private double _splitterStartX;
    private double _splitterStartWidth;

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var el = (UIElement)sender;
        el.CapturePointer(e.Pointer);
        _splitterDragging   = true;
        _splitterStartX     = e.GetCurrentPoint(null).Position.X;
        _splitterStartWidth = ContentGrid.ColumnDefinitions[0].ActualWidth;
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        var col   = ContentGrid.ColumnDefinitions[0];
        var x     = e.GetCurrentPoint(null).Position.X;
        var delta = x - _splitterStartX;
        var newW  = Math.Clamp(_splitterStartWidth + delta, col.MinWidth, col.MaxWidth);
        col.Width = new GridLength(newW);
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }
}
