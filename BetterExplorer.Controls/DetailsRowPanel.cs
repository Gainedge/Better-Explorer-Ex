using System.Linq;
using BetterExplorer.ShellApi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BetterExplorer.Controls;

/// <summary>
/// A Panel that lays out its children side-by-side in the order defined by
/// <see cref="DetailsColumnSettings.Columns"/>.  Each child must have its
/// <see cref="FrameworkElement.Tag"/> set to the column <c>Key</c> string so
/// the panel can match it to the right slot.  Children whose Tag is not found
/// in the current column list are hidden.
///
/// Using a dedicated panel instead of a fixed-column Grid means:
/// – The column count is fully dynamic (add/remove columns at runtime).
/// – Column order reflows automatically when <c>Columns</c> is rearranged.
/// – Width changes on any column immediately invalidate measure/arrange.
/// </summary>
public sealed class DetailsRowPanel : Panel
{
    // ── Attached property ─────────────────────────────────────────────────

    /// <summary>
    /// Attached property set on a <see cref="DetailsRowPanel"/> to point to the
    /// shared <see cref="DetailsColumnSettings"/> resource.
    /// </summary>
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.RegisterAttached(
            "Columns",
            typeof(DetailsColumnSettings),
            typeof(DetailsRowPanel),
            new PropertyMetadata(null, OnColumnsChanged));

    public static void SetColumns(DependencyObject obj, DetailsColumnSettings value) =>
        obj.SetValue(ColumnsProperty, value);

    public static DetailsColumnSettings? GetColumns(DependencyObject obj) =>
        (DetailsColumnSettings?)obj.GetValue(ColumnsProperty);

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DetailsRowPanel panel) return;

        // Unsubscribe from old settings.
        if (e.OldValue is DetailsColumnSettings old)
        {
            old.PropertyChanged -= panel.OnColumnSettingsChanged;
            foreach (var col in old.Columns)
                col.PropertyChanged -= panel.OnColumnPropertyChanged;
            old.Columns.CollectionChanged -= panel.OnColumnsCollectionChanged;
        }

        // Subscribe to new settings.
        if (e.NewValue is DetailsColumnSettings settings)
        {
            settings.PropertyChanged      += panel.OnColumnSettingsChanged;
            settings.Columns.CollectionChanged += panel.OnColumnsCollectionChanged;
            foreach (var col in settings.Columns)
                col.PropertyChanged += panel.OnColumnPropertyChanged;
        }

        panel.InvalidateMeasure();
    }

    // ── Change handlers ───────────────────────────────────────────────────

    private void OnColumnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        InvalidateMeasure();

    private void OnColumnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailsColumn.Width))
            InvalidateMeasure();
    }

    private void OnColumnsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Move: column objects are the same, just reordered. MeasureOverride reads
        // the Columns list in order, so a single InvalidateMeasure() is sufficient.
        // No subscription changes needed — the same objects are still in the list.
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            InvalidateMeasure();
            return;
        }

        // For Add events subscribe; for Remove events unsubscribe width listeners.
        if (e.NewItems != null)
            foreach (DetailsColumn col in e.NewItems)
                col.PropertyChanged += OnColumnPropertyChanged;
        if (e.OldItems != null)
            foreach (DetailsColumn col in e.OldItems)
                col.PropertyChanged -= OnColumnPropertyChanged;

        InvalidateMeasure();
    }

    // ── Layout ────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var settings = GetColumns(this);
        if (settings == null)
        {
            foreach (UIElement child in Children)
                child.Measure(new Size(0, availableSize.Height));
            return new Size(0, availableSize.Height);
        }

        double totalWidth = 0;
        double maxHeight  = 0;

        foreach (var col in settings.Columns)
        {
            totalWidth += col.Width;
            var child = FindChild(col.Key);
            if (child == null) continue;
            child.Visibility = Visibility.Visible;
            child.Measure(new Size(col.Width, availableSize.Height));
            maxHeight = System.Math.Max(maxHeight, child.DesiredSize.Height);
        }

        // Hide children that have no matching column.
        foreach (UIElement child in Children)
        {
            var key = (child as FrameworkElement)?.Tag as string;
            if (key == null || !settings.Columns.Any(c => c.Key == key))
                child.Visibility = Visibility.Collapsed;
        }

        return new Size(totalWidth, double.IsInfinity(availableSize.Height) ? maxHeight : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var settings = GetColumns(this);
        if (settings == null)
        {
            foreach (UIElement child in Children)
                child.Arrange(new Rect(0, 0, 0, finalSize.Height));
            return finalSize;
        }

        double x = 0;
        foreach (var col in settings.Columns)
        {
            var child = FindChild(col.Key);
            if (child != null)
                child.Arrange(new Rect(x, 0, col.Width, finalSize.Height));
            x += col.Width;
        }

        return new Size(x, finalSize.Height);
    }

    private FrameworkElement? FindChild(string key)
    {
        foreach (UIElement child in Children)
            if (child is FrameworkElement fe && fe.Tag is string k && k == key)
                return fe;
        return null;
    }
}
