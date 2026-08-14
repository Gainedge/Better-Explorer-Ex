using System.Collections.Specialized;
using System.ComponentModel;
using BetterExplorer.ShellApi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BetterExplorer.Controls;

/// <summary>
/// A row control for the Details view that builds its cells dynamically from
/// <see cref="DetailsColumnSettings.Columns"/>. Adding or removing a column at runtime
/// triggers a full cell rebuild so the row always reflects the current column list.
/// Width changes are applied cheaply in-place without a full rebuild.
/// </summary>
public sealed partial class DetailsRowControl : UserControl
{
    // ── Dependency properties ─────────────────────────────────────────────

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(ShellItem), typeof(DetailsRowControl),
            new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(DetailsColumnSettings), typeof(DetailsRowControl),
            new PropertyMetadata(null, OnColumnsChanged));

    public ShellItem? Item
    {
        get => (ShellItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public DetailsColumnSettings? Columns
    {
        get => (DetailsColumnSettings?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    // ── Fields ────────────────────────────────────────────────────────────

    private DetailsRowPanel? _panel;

    // ── Constructor ───────────────────────────────────────────────────────

    public DetailsRowControl()
    {
        InitializeComponent();
        _panel = new DetailsRowPanel();
        Content = _panel;
    }

    // ── Property change handlers ──────────────────────────────────────────

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DetailsRowControl ctrl)
        {
            // Unsubscribe from old item's property changes.
            if (e.OldValue is ShellItem oldItem)
                oldItem.PropertyChanged -= ctrl.OnItemPropertyChanged;

            // Subscribe to new item so opacity/icon/label updates propagate.
            if (e.NewValue is ShellItem newItem)
                newItem.PropertyChanged += ctrl.OnItemPropertyChanged;

            ctrl.RebuildCells();
        }
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DetailsRowControl ctrl)
        {
            // Unsubscribe old settings.
            if (e.OldValue is DetailsColumnSettings old)
            {
                old.Columns.CollectionChanged -= ctrl.OnColumnsCollectionChanged;
                foreach (var col in old.Columns)
                    col.PropertyChanged -= ctrl.OnColumnPropertyChanged;
            }

            // Subscribe to new settings.
            if (e.NewValue is DetailsColumnSettings settings)
            {
                settings.Columns.CollectionChanged += ctrl.OnColumnsCollectionChanged;
                foreach (var col in settings.Columns)
                    col.PropertyChanged += ctrl.OnColumnPropertyChanged;

                // Wire the panel to the new settings so it lays out correctly.
                if (ctrl._panel != null)
                    DetailsRowPanel.SetColumns(ctrl._panel, settings);
            }

            ctrl.RebuildCells();
        }
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Move events: the same column objects just changed position.
        // DetailsRowPanel.MeasureOverride reads Columns in order, so
        // InvalidateMeasure() is enough — no subscription changes, no cell rebuild.
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            _panel?.InvalidateMeasure();
            return;
        }

        // Add: subscribe width listener for new columns.
        if (e.NewItems != null)
            foreach (DetailsColumn col in e.NewItems)
                col.PropertyChanged += OnColumnPropertyChanged;

        // Remove: unsubscribe. Guard against duplicate removal (shouldn't happen, but safe).
        if (e.OldItems != null)
            foreach (DetailsColumn col in e.OldItems)
                col.PropertyChanged -= OnColumnPropertyChanged;

        // Reset / Add / Remove all require a full cell rebuild.
        RebuildCells();
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Width changes are handled by DetailsRowPanel's own listener (invalidates measure).
        // Nothing extra to do here unless we want to update clipping etc.
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_panel == null || Item == null) return;

        switch (e.PropertyName)
        {
            case nameof(ShellItem.IconOpacity):
                _panel.Opacity = Item.IconOpacity;
                break;

            case nameof(ShellItem.Icon):
            case nameof(ShellItem.IconFallbackVisibility):
                UpdateIconCell();
                break;

            case nameof(ShellItem.OverlayIcon):
            case nameof(ShellItem.OverlayIconVisibility):
                UpdateOverlayCell();
                break;

            case nameof(ShellItem.LabelVisibility):
            case nameof(ShellItem.Name):
            case nameof(ShellItem.DisplayName):
                UpdateNameText();
                break;

            case nameof(ShellItem.DateModifiedString):
                UpdateTextCell("Date");
                break;

            case nameof(ShellItem.ItemType):
                UpdateTextCell("Type");
                break;

            case nameof(ShellItem.Size):
                UpdateTextCell("Size");
                break;
        }
    }

    // ── Cell build ────────────────────────────────────────────────────────

    private void RebuildCells()
    {
        if (_panel == null) return;
        _panel.Children.Clear();

        var settings = Columns;
        var item     = Item;
        if (settings == null || item == null)
            return;

        _panel.Opacity = item.IconOpacity;

        foreach (var col in settings.Columns)
            _panel.Children.Add(BuildCell(col, item));
    }

    private FrameworkElement BuildCell(DetailsColumn col, ShellItem item)
    {
        var border = new Border
        {
            Tag     = col.Key,
            Padding = col.Key == "Name"
                ? new Thickness(0, 0, 8, 0)
                : new Thickness(6, 0, 8, 0),
        };

        if (col.Key == "Name")
            border.Child = BuildNameCell(item);
        else
            border.Child = BuildTextCell(col.Key, item);

        return border;
    }

    // ── Name cell (icon + label) ──────────────────────────────────────────

    private Grid BuildNameCell(ShellItem item)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── Icon viewbox ──────────────────────────────────────────────────
        var iconRoot = new Grid { Width = 16, Height = 16 };

        var fallback = new FontIcon
        {
            FontSize           = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        SetBinding(fallback, FontIcon.GlyphProperty, item, nameof(ShellItem.IconGlyph));
        SetBinding(fallback, UIElement.VisibilityProperty, item, nameof(ShellItem.IconFallbackVisibility));
        iconRoot.Children.Add(fallback);

        var iconImage = new Image { Stretch = Stretch.Uniform, Tag = "icon" };
        SetBinding(iconImage, Image.SourceProperty, item, nameof(ShellItem.Icon));
        iconRoot.Children.Add(iconImage);

        var overlayImage = new Image
        {
            Width               = 24,
            Height              = 24,
            Stretch             = Stretch.Uniform,
            IsHitTestVisible    = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Tag                 = "overlay",
        };
        SetBinding(overlayImage, Image.SourceProperty,     item, nameof(ShellItem.OverlayIcon));
        SetBinding(overlayImage, UIElement.VisibilityProperty, item, nameof(ShellItem.OverlayIconVisibility));
        iconRoot.Children.Add(overlayImage);

        var viewbox = new Viewbox
        {
            Width              = 16,
            Height             = 16,
            Stretch            = Stretch.Uniform,
            StretchDirection   = StretchDirection.DownOnly,
            VerticalAlignment  = VerticalAlignment.Center,
            Child              = iconRoot,
        };
        Grid.SetColumn(viewbox, 0);
        grid.Children.Add(viewbox);

        // ── Label ─────────────────────────────────────────────────────────
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxLines          = 1,
        };
        SetBinding(label, TextBlock.TextProperty,       item, nameof(ShellItem.DisplayName));
        SetBinding(label, UIElement.VisibilityProperty, item, nameof(ShellItem.LabelVisibility));
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        return grid;
    }

    // ── Plain text cell ───────────────────────────────────────────────────

    private TextBlock BuildTextCell(string key, ShellItem item)
    {
        var tb = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxLines          = 1,
        };

        if (key == "Size")
            tb.HorizontalAlignment = HorizontalAlignment.Right;

        var prop = key switch
        {
            "Date" => nameof(ShellItem.DateModifiedString),
            "Type" => nameof(ShellItem.ItemType),
            "Size" => nameof(ShellItem.Size),
            _      => key, // custom columns: key matches property name
        };
        SetBinding(tb, TextBlock.TextProperty, item, prop);

        return tb;
    }

    // ── Targeted refresh helpers (avoid full rebuild on every property change) ──

    private void UpdateIconCell()
    {
        if (_panel == null || Item == null) return;
        foreach (UIElement child in _panel.Children)
        {
            if (child is not Border b || b.Tag as string != "Name") continue;
            if (b.Child is not Grid g) continue;
            foreach (var gc in g.Children)
            {
                if (gc is not Viewbox vb || vb.Child is not Grid iconRoot) continue;
                foreach (var ic in iconRoot.Children)
                {
                    if (ic is Image img && (string?)img.Tag == "icon")
                    {
                        img.Source = Item.Icon;
                    }
                    if (ic is FontIcon fi)
                    {
                        fi.Glyph      = Item.IconGlyph;
                        fi.Visibility = Item.IconFallbackVisibility;
                    }
                }
                break;
            }
            break;
        }
    }

    private void UpdateOverlayCell()
    {
        if (_panel == null || Item == null) return;
        foreach (UIElement child in _panel.Children)
        {
            if (child is not Border b || b.Tag as string != "Name") continue;
            if (b.Child is not Grid g) continue;
            foreach (var gc in g.Children)
            {
                if (gc is not Viewbox vb || vb.Child is not Grid iconRoot) continue;
                foreach (var ic in iconRoot.Children)
                {
                    if (ic is Image img && (string?)img.Tag == "overlay")
                    {
                        img.Source     = Item.OverlayIcon;
                        img.Visibility = Item.OverlayIconVisibility;
                    }
                }
                break;
            }
            break;
        }
    }

    private void UpdateNameText()
    {
        if (_panel == null || Item == null) return;
        foreach (UIElement child in _panel.Children)
        {
            if (child is not Border b || b.Tag as string != "Name") continue;
            if (b.Child is not Grid g) continue;
            foreach (var gc in g.Children)
            {
                if (gc is TextBlock tb)
                {
                    tb.Text       = Item.DisplayName;
                    tb.Visibility = Item.LabelVisibility;
                }
            }
            break;
        }
    }

    private void UpdateTextCell(string key)
    {
        if (_panel == null || Item == null) return;
        foreach (UIElement child in _panel.Children)
        {
            if (child is not Border b || b.Tag as string != key) continue;
            if (b.Child is TextBlock tb)
            {
                tb.Text = key switch
                {
                    "Date" => Item.DateModifiedString,
                    "Type" => Item.ItemType,
                    "Size" => Item.Size,
                    _      => string.Empty,
                };
            }
            break;
        }
    }

    // ── Binding helper ────────────────────────────────────────────────────

    private static void SetBinding(
        FrameworkElement target,
        DependencyProperty property,
        object source,
        string path)
    {
        target.SetBinding(property, new Binding
        {
            Source = source,
            Path   = new PropertyPath(path),
            Mode   = BindingMode.OneWay,
        });
    }
}
