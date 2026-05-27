using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterExplorer.ShellApi;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace BetterExplorer.Controls;

/// <summary>
/// Windows-Explorer–style breadcrumb address bar.
///
/// Breadcrumb mode:
///   • One chip per path segment.  Clicking a chip navigates to that path.
///   • Each chip has a chevron that drops down a flyout listing the sibling
///     folders at the same level, each with a shell icon and acrylic background.
///   • The leftmost root-menu button (☰) lists This PC / Desktop / Downloads /
///     Documents / OneDrive for quick-jump navigation.
///   • Clicking the empty area right of the chips enters edit mode.
///   • Pressing F2 anywhere in the control enters edit mode.
///
/// Edit mode:
///   • AutoSuggestBox with path history dropdown.
///   • Typing suggests sub-folders of the deepest existing segment (live).
///   • Enter → navigate and return to breadcrumb mode.
///   • Escape → cancel and return to breadcrumb mode.
/// </summary>
public sealed partial class ShellBreadcrumbBar : UserControl
{
    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when the user navigates to a file-system path.</summary>
    public event EventHandler<string>? PathRequested;

    // ── State ─────────────────────────────────────────────────────────────────

    private string _currentPath = string.Empty;

    // History of paths the user typed manually (newest first, max 50).
    private readonly List<string> _history = [];

    // Small icon cache (path → bitmap)
    private readonly Dictionary<string, WriteableBitmap?> _iconCache =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Quick-access root entries ─────────────────────────────────────────────

    private static readonly (string Label, Func<string?> GetPath)[] QuickRoots =
    [
        ("Desktop",   () => Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
        ("Documents", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        ("Downloads", () => NativeShell.SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"))),
        ("Pictures",  () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        ("Music",     () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
        ("Videos",    () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        ("OneDrive",  () => Environment.GetEnvironmentVariable("OneDriveConsumer")
                         ?? Environment.GetEnvironmentVariable("OneDrive")),
    ];

    // ── Constructor ───────────────────────────────────────────────────────────

    public ShellBreadcrumbBar()
    {
        _outsideClickHandler = new PointerEventHandler(OnOutsidePointerPressed);

        InitializeComponent();

        // F2 enters edit mode from anywhere on the bar.
        KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.F2) EnterEditMode();
        };

        // Rebuild chips whenever the app theme changes so foreground colors update.
        ActualThemeChanged += (_, _) => RebuildChips();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the displayed path without raising <see cref="PathRequested"/>.
    /// Call this from the host's PathChanged handler.
    /// </summary>
    public void SetPath(string path)
    {
        _currentPath = path;
        if (EditPanel.Visibility == Visibility.Collapsed)
            RebuildChips();
    }

    // ── Breadcrumb chip building ───────────────────────────────────────────────

    private void RebuildChips()
    {
        ChipPanel.Children.Clear();
        if (string.IsNullOrEmpty(_currentPath)) return;

        // Update the root button icon to show the current location's icon.
        _ = UpdateRootMenuIconAsync(_currentPath);

        var segments = BuildSegments(_currentPath);
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            bool isLast = i == segments.Count - 1;

            // ── Segment label button ───────────────────────────────────────
            var lbl = new Button
            {
                Style   = (Style)Resources["BreadcrumbSegmentStyle"],
                Content = new TextBlock
                {
                    Text              = seg.DisplayName,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize          = 12,
                    FontFamily        = new FontFamily("Segoe UI Variable Text"),
                    // Let foreground inherit from the visual tree (theme-aware).
                    // Dim non-terminal segments with opacity to mimic secondary text color.
                    Opacity           = isLast ? 1.0 : 0.65,
                },
                Tag = seg.FullPath,
            };
            lbl.Click += OnSegmentClick;
            ChipPanel.Children.Add(lbl);

            // ── Chevron dropdown ───────────────────────────────────────────
            var chevron = new Button
            {
                Style   = (Style)Resources["BreadcrumbChevronStyle"],
                Content = new FontIcon
                {
                    Glyph    = "\uE76C",  // ChevronRight
                    FontSize = 8,
                    // Inherit foreground; dim to approximate tertiary text color.
                    Opacity  = 0.45,
                },
                Tag = seg.FullPath,
            };
            chevron.Click += OnChevronClick;
            ChipPanel.Children.Add(chevron);
        }

        // Scroll to end so the deepest segment is always visible.
        ChipScroller.UpdateLayout();
        ChipScroller.ChangeView(ChipScroller.ScrollableWidth, null, null, true);
    }

    // ── Path → segment list ───────────────────────────────────────────────────

    private record Segment(string DisplayName, string FullPath);

    private static List<Segment> BuildSegments(string path)
    {
        var shellChain = NativeShell.BuildShellBreadcrumbs(path);
        var result = new List<Segment>(shellChain.Count);

        // Filter out the Desktop root (it's a parent of all, but not shown in breadcrumbs).
        // Desktop has the special GUID {B4BFCC3A-DB86-4CBB-9D57-7EB81F2D2D34} in its parsing name.
        // If we have multiple items and the first one is Desktop, skip it.
        int start = 0;
        if (shellChain.Count > 1)
        {
            var firstParsing = shellChain[0].ParsingName;
            // Check for Desktop GUID or Desktop folder path.
            if (firstParsing.Contains("B4BFCC3A", StringComparison.OrdinalIgnoreCase) ||
                firstParsing.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), 
                    StringComparison.OrdinalIgnoreCase))
            {
                start = 1;
            }
        }

        for (int i = start; i < shellChain.Count; i++)
        {
            var seg = shellChain[i];
            // Use the virtual-root helper to ensure display names are correct for virtual items.
            string displayName = seg.ParsingName.StartsWith("::", StringComparison.Ordinal)
                ? NativeShell.GetVirtualRootDisplayName(seg.ParsingName)
                : seg.DisplayName;
            result.Add(new Segment(displayName, seg.ParsingName));
        }

        // If the shell chain resolution failed for a virtual path (e.g. ::{GUID}),
        // synthesise a single chip so the bar never goes blank.
        if (result.Count == 0 && path.StartsWith("::{", StringComparison.Ordinal))
        {
            var displayName = NativeShell.GetVirtualRootDisplayName(path);
            result.Add(new Segment(displayName, path));
        }

        return result;
    }

    // ── Chip click → navigate ─────────────────────────────────────────────────

    private void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string p })
            Navigate(p);
    }

    // ── Chevron click → sibling folder flyout ─────────────────────────────────

    private async void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button chevron) return;
        if (chevron.Tag is not string segPath) return;

        // Use the shell to decide what to enumerate for the chevron dropdown.
        // For the deepest segment we list its own sub-folders.
        // For any other segment we list the sub-folders of that segment (siblings at that level).
        string enumeratePath = segPath;
        if (string.IsNullOrEmpty(enumeratePath)) return;

        var flyout  = BuildAcrylicFlyout();
        var content = new StackPanel { Spacing = 2, Padding = new Thickness(4) };
        flyout.Content = content;

        // Show a loading placeholder while enumerating.
        content.Children.Add(new TextBlock
        {
            Text      = "Loading…",
            Opacity   = 0.5,
            FontSize  = 12,
            Margin    = new Thickness(8, 4, 8, 4),
        });

        flyout.ShowAt(chevron);

        // Enumerate siblings on a background thread.
        var siblings = await Task.Run(() => GetSubfolders(enumeratePath));

        content.Children.Clear();
        if (siblings.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text   = "(no sub-folders)",
                Opacity = 0.5,
                FontSize = 12,
                Margin = new Thickness(8, 4, 8, 4),
            });
            return;
        }

        foreach (var (name, fullPath) in siblings)
        {
            var row = BuildFlyoutRow(name, fullPath, fullPath, flyout);
            content.Children.Add(row);
            _ = LoadIconForRowAsync(row, fullPath);
        }
    }

    // ── Root menu button → quick-jump flyout ──────────────────────────────────

    private void OnRootMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var flyout  = BuildAcrylicFlyout();
        var content = new StackPanel { Spacing = 2, Padding = new Thickness(4) };
        var scroller = new ScrollViewer
        {
            Content = content,
            MaxHeight = 340,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };
        flyout.Content = scroller;

        // Enumerate drives via the shell (This PC children) for correct display names.
        var thisPcParsing = $"::{{{NativeShell.FOLDERID_ComputerFolder}}}";
        var drives = NativeShell.GetShellSubfolders(thisPcParsing);
        foreach (var (displayName, parsingName) in drives)
        {
            var row = BuildFlyoutRow(displayName, parsingName, parsingName, flyout);
            content.Children.Add(row);
            _ = LoadIconForRowAsync(row, parsingName);
        }

        // Separator
        content.Children.Add(new Border
        {
            Height     = 1,
            Margin     = new Thickness(8, 4, 8, 4),
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        });

        // Quick-access locations
        foreach (var (label, getPath) in QuickRoots)
        {
            var p = getPath();
            if (string.IsNullOrEmpty(p) || !System.IO.Directory.Exists(p)) continue;
            var row = BuildFlyoutRow(label, p, p, flyout);
            content.Children.Add(row);
            _ = LoadIconForRowAsync(row, p);
        }

        flyout.ShowAt(btn);
    }

    // ── Shared flyout helpers ─────────────────────────────────────────────────

    private static Flyout BuildAcrylicFlyout()
    {
        return new Flyout
        {
            Placement            = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            FlyoutPresenterStyle = BuildFlyoutPresenterStyle(),
        };
    }

    private static Style BuildFlyoutPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 200.0));
        return style;
    }

    /// <summary>Builds one row in a sibling/quick-jump flyout.</summary>
    private Button BuildFlyoutRow(string name, string navigatePath, string iconPath, Flyout flyout)
    {
        var icon = new Image
        {
            Width  = 16,
            Height = 16,
            Stretch = Stretch.UniformToFill,
            Tag    = iconPath,     // used later by LoadIconForRowAsync
        };

        var row = new Button
        {
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background     = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding        = new Thickness(8, 5, 8, 5),
            Tag            = navigatePath,
            Content        = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                Children    = { icon, new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 } }
            },
        };

        row.Click += (_, _) =>
        {
            flyout.Hide();
            Navigate(navigatePath);
        };

        return row;
    }

    private async Task LoadIconForRowAsync(Button row, string path)
    {
        // Find the Image inside the row's StackPanel.
        if (row.Content is not StackPanel sp) return;
        if (sp.Children.FirstOrDefault(c => c is Image) is not Image img) return;

        WriteableBitmap? bmp;
        if (!_iconCache.TryGetValue(path, out bmp))
        {
            // Extract raw pixels on a background thread (safe — no WinRT UI objects created).
            var (pixels, w, h) = await Task.Run(() =>
            {
                var hbm = NativeShell.TryGetShellHBitmap(path, 16, NativeShell.SIIGBF.IconOnly);
                if (hbm == IntPtr.Zero) return (null, 0, 0);
                try   { return NativeShell.HBitmapToPixels(hbm); }
                finally { NativeShell.DeleteObject(hbm); }
            });

            // Create WriteableBitmap back on the UI thread — WinRT COM requirement.
            bmp = NativeShell.PixelsToBitmapSync(pixels!, w, h);
            _iconCache[path] = bmp;
        }

        if (bmp != null) img.Source = bmp;
    }

    private async Task UpdateRootMenuIconAsync(string path)
    {
        WriteableBitmap? bmp;
        if (!_iconCache.TryGetValue(path, out bmp))
        {
            var (pixels, w, h) = await Task.Run(() =>
            {
                var hbm = NativeShell.TryGetShellHBitmap(path, 16, NativeShell.SIIGBF.IconOnly);
                if (hbm == IntPtr.Zero) return (null, 0, 0);
                try   { return NativeShell.HBitmapToPixels(hbm); }
                finally { NativeShell.DeleteObject(hbm); }
            });

            bmp = NativeShell.PixelsToBitmapSync(pixels!, w, h);
            _iconCache[path] = bmp;
        }

        if (bmp != null) RootMenuIcon.Source = bmp;
    }

    // ── Search mode chip ──────────────────────────────────────────────────────

    /// <summary>
    /// Replaces all breadcrumb chips with a single non-navigable search indicator:
    ///   🔍  Search results for "query" in [folder name]
    /// Calling <see cref="SetPath"/> afterwards restores normal breadcrumb mode.
    /// </summary>
    public void SetSearchMode(string query)
    {
        ChipPanel.Children.Clear();

        // Derive a short display name for the folder being searched.
        string folderLabel = string.Empty;
        if (!string.IsNullOrEmpty(_currentPath))
        {
            var segments = BuildSegments(_currentPath);
            if (segments.Count > 0)
                folderLabel = segments[segments.Count - 1].DisplayName;
        }

        var label = string.IsNullOrEmpty(folderLabel)
            ? $"Search results for \"{query}\""
            : $"Search results for \"{query}\" in {folderLabel}";

        // Build the chip content: search icon + label text side-by-side.
        var icon = new FontIcon
        {
            Glyph      = "\uE721",   // Search glyph
            FontSize   = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        var text = new TextBlock
        {
            Text              = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize          = 12,
            FontFamily        = new FontFamily("Segoe UI Variable Text"),
            Foreground        = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
            Children    = { icon, text },
        };

        // Use Button (correct TargetType for BreadcrumbSegmentStyle) but without
        // a Click handler so it acts as a static display chip.
        var chip = new Button
        {
            Style            = (Style)Resources["BreadcrumbSegmentStyle"],
            Content          = content,
            IsHitTestVisible = false,
        };

        ChipPanel.Children.Add(chip);

        ChipScroller.UpdateLayout();
        ChipScroller.ChangeView(0, null, null, true);   // scroll to start — chip fills the bar
    }

    // ── Sub-folder enumeration ────────────────────────────────────────────────

    private static List<(string Name, string FullPath)> GetSubfolders(string parent)
    {
        var shellResult = NativeShell.GetShellSubfolders(parent);
        return shellResult.ConvertAll(x => (x.DisplayName, x.ParsingName));
    }

    // ── Edit mode ─────────────────────────────────────────────────────────────

    /// <summary>Switches the bar from breadcrumb display to text-edit mode.</summary>
    public void EnterEditMode()
    {
        EditBox.Text = _currentPath;
        // Move caret to end so user can keep typing immediately.
        EditBox.SelectionStart = EditBox.Text.Length;

        EditPanel.Visibility      = Visibility.Visible;
        BreadcrumbView.Visibility = Visibility.Collapsed;

        EditBox.Focus(FocusState.Programmatic);

        // Dismiss edit mode when the user clicks anywhere outside this control.
        if (XamlRoot?.Content is UIElement root)
            root.AddHandler(PointerPressedEvent, _outsideClickHandler, handledEventsToo: true);
    }

    private void LeaveEditMode()
    {
        SuggestPopup.IsOpen = false;
        SuggestList.ItemsSource = null;

        if (XamlRoot?.Content is UIElement root)
            root.RemoveHandler(PointerPressedEvent, _outsideClickHandler);

        EditPanel.Visibility      = Visibility.Collapsed;
        BreadcrumbView.Visibility = Visibility.Visible;
    }

    private readonly PointerEventHandler _outsideClickHandler;

    private void OnOutsidePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(this).Position;
        if (pos.X >= 0 && pos.Y >= 0 && pos.X <= ActualWidth && pos.Y <= ActualHeight)
            return;
        LeaveEditMode();
    }

    // Click on the breadcrumb strip's empty space enters edit mode.
    private void OnBreadcrumbViewPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.Handled) EnterEditMode();
    }

    // ── TextBox + Popup suggestion callbacks ──────────────────────────────────

    private void OnEditBoxTextChanged(object sender, TextChangedEventArgs args)
    {
        var typed = EditBox.Text;
        var suggestions = new List<string>();

        suggestions.AddRange(
            _history.Where(h => h.StartsWith(typed, StringComparison.OrdinalIgnoreCase)));

        var dirPart = typed;
        if (!Directory.Exists(dirPart))
            dirPart = Path.GetDirectoryName(typed) ?? string.Empty;

        if (Directory.Exists(dirPart))
        {
            try
            {
                var leaf = typed.Length > dirPart.Length
                    ? typed[dirPart.Length..].TrimStart('\\', '/')
                    : string.Empty;

                foreach (var sub in Directory.EnumerateDirectories(dirPart))
                {
                    if (leaf.Length == 0 ||
                        Path.GetFileName(sub).StartsWith(leaf, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!suggestions.Contains(sub, StringComparer.OrdinalIgnoreCase))
                            suggestions.Add(sub);
                        if (suggestions.Count >= 12) break;
                    }
                }
            }
            catch { }
        }

        SuggestList.ItemsSource = suggestions;

        if (suggestions.Count > 0)
        {
            // Position popup directly below this control.
            SuggestList.MinWidth  = ActualWidth;
            SuggestPopup.HorizontalOffset = 0;
            SuggestPopup.VerticalOffset   = ActualHeight;
            SuggestPopup.IsOpen = true;
        }
        else
        {
            SuggestPopup.IsOpen = false;
        }
    }

    private void OnSuggestItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string chosen)
        {
            EditBox.Text = chosen;
            EditBox.SelectionStart = chosen.Length;
            SuggestPopup.IsOpen = false;
            CommitPath(chosen);
        }
    }

    private void OnEditBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                LeaveEditMode();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
            {
                // If popup is open and an item is selected, accept it.
                string? path = SuggestList.SelectedItem as string ?? EditBox.Text;
                path = path.Trim().Trim('"');
                path = Environment.ExpandEnvironmentVariables(path);
                SuggestPopup.IsOpen = false;
                CommitPath(path);
                e.Handled = true;
                break;
            }

            case VirtualKey.Down:
                // Move selection into the suggestion list.
                if (SuggestPopup.IsOpen && SuggestList.Items.Count > 0)
                {
                    var next = SuggestList.SelectedIndex < SuggestList.Items.Count - 1
                        ? SuggestList.SelectedIndex + 1 : 0;
                    SuggestList.SelectedIndex = next;
                    SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                }
                e.Handled = true;
                break;

            case VirtualKey.Up:
                if (SuggestPopup.IsOpen && SuggestList.Items.Count > 0)
                {
                    var prev = SuggestList.SelectedIndex > 0
                        ? SuggestList.SelectedIndex - 1 : SuggestList.Items.Count - 1;
                    SuggestList.SelectedIndex = prev;
                    SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }

    private void CommitPath(string path)
    {
        if (string.IsNullOrEmpty(path)) { LeaveEditMode(); return; }
        AddToHistory(path);
        Navigate(path);
        LeaveEditMode();
    }

    // ── History management ────────────────────────────────────────────────────

    private void AddToHistory(string path)
    {
        _history.Remove(path);
        _history.Insert(0, path);
        if (_history.Count > 50)
            _history.RemoveAt(_history.Count - 1);
    }

    // ── Navigation helper ─────────────────────────────────────────────────────

    private void Navigate(string path)
    {
        _currentPath = path;
        RebuildChips();
        PathRequested?.Invoke(this, path);
    }
}
