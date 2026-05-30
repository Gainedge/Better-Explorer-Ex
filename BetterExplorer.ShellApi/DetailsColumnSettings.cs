using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace BetterExplorer.ShellApi;

/// <summary>
/// Shared column-width state for the Details view.
/// Stored as a XAML resource so both the header and the item DataTemplate can bind to it.
/// </summary>
public sealed class DetailsColumnSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _updating;

    /// <summary>Ordered list of all visible columns. Add to this list to introduce new columns.</summary>
    public ObservableCollection<DetailsColumn> Columns { get; }

    public DetailsColumnSettings()
    {
        Columns =
        [
            new DetailsColumn { Key = "Name", Header = "Name",          Width = 280, MinWidth = 80  },
            new DetailsColumn { Key = "Date", Header = "Date modified",  Width = 160, MinWidth = 60  },
            new DetailsColumn { Key = "Type", Header = "Type",           Width = 140, MinWidth = 60  },
            new DetailsColumn { Key = "Size", Header = "Size",           Width = 90,  MinWidth = 50  },
        ];
        Columns.CollectionChanged += OnColumnsChanged;
        foreach (var col in Columns)
            col.PropertyChanged += OnColumnPropertyChanged;
    }

    // ── Batch update support ──────────────────────────────────────────────────
    // Suppress intermediate notifications while rebuilding column order/widths;
    // fire a single full refresh when done.  This eliminates the N*4 layout
    // passes that used to happen when ApplyFolderSettings did Clear+Add in a loop.

    /// <summary>
    /// Atomically updates column order and widths.
    /// – Width-only change (most navigations): mutates backing fields directly,
    ///   fires exactly 4 width notifications. Zero collection events, zero subscription churn.
    /// – Order change: reorders via <see cref="ObservableCollection{T}.Move"/> so the same
    ///   column objects stay in the collection — no subscribe/unsubscribe storm.
    /// – No change: returns immediately (true no-op).
    /// </summary>
    public void ApplyColumns(System.Collections.Generic.IReadOnlyList<(string Key, double Width)> newOrder)
    {
        if (newOrder.Count != Columns.Count)
        {
            // Column schema changed (add/remove) — rare; fall through to full rebuild below.
            goto FullRebuild;
        }

        // Check whether order and/or widths differ.
        bool orderSame = true;
        bool widthSame = true;
        for (int i = 0; i < newOrder.Count; i++)
        {
            if (Columns[i].Key != newOrder[i].Key)  { orderSame = false; widthSame = false; break; }
            if (Math.Abs(Columns[i].Width - newOrder[i].Width) > 0.5) widthSame = false;
        }

        if (orderSame && widthSame) return; // nothing changed

        if (orderSame)
        {
            // ── Width-only fast path ─────────────────────────────────────────
            // Set backing fields directly so no PropertyChanged fires per column;
            // fire one consolidated RaiseAllWidths() at the end.
            _updating = true;
            try
            {
                for (int i = 0; i < newOrder.Count; i++)
                    Columns[i]._width = Math.Max(Columns[i].MinWidth, newOrder[i].Width);
            }
            finally { _updating = false; }
            foreach (var col in Columns) col.RaiseWidth();
            RaiseAllWidths();
            return;
        }

        // ── Order (+ optional width) change
        // Update widths via backing fields first (no events).
        _updating = true;
        try
        {
            foreach (var (key, width) in newOrder)
            {
                for (int i = 0; i < Columns.Count; i++)
                    if (Columns[i].Key == key)
                    { Columns[i]._width = Math.Max(Columns[i].MinWidth, width); break; }
            }

            // Reorder via Move() — fires CollectionChanged(Move) events.
            // Move keeps the same column OBJECTS in the collection, so external
            // subscribers never lose or gain a subscription. This is the key
            // difference from Clear+Add, which leaked N subscriptions per navigation.
            for (int target = 0; target < newOrder.Count; target++)
            {
                var key = newOrder[target].Key;
                int current = -1;
                for (int i = target; i < Columns.Count; i++)
                    if (Columns[i].Key == key) { current = i; break; }
                if (current > target)
                    Columns.Move(current, target);
            }
        }
        finally { _updating = false; }
        foreach (var col in Columns) col.RaiseWidth();
        RaiseAllOrders();
        RaiseAllWidths();
        return;

        FullRebuild:
        // Schema change (columns added/removed) — full Clear+Add path.
        // This only happens when the column schema itself changes, not on normal navigation.
        _updating = true;
        try
        {
            foreach (var col in Columns)
                col.PropertyChanged -= OnColumnPropertyChanged;
            var pool = new System.Collections.Generic.Dictionary<string, DetailsColumn>(Columns.Count);
            foreach (var col in Columns) pool[col.Key] = col;
            Columns.Clear();
            foreach (var (key, width) in newOrder)
            {
                if (!pool.TryGetValue(key, out var col)) continue;
                col._width = Math.Max(col.MinWidth, width);
                Columns.Add(col);
                col.PropertyChanged += OnColumnPropertyChanged;
            }
        }
        finally { _updating = false; }
        foreach (var col in Columns) col.RaiseWidth();
        RaiseAllOrders();
        RaiseAllWidths();
    }

    private void RaiseAllOrders()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DateOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeOrder)));
    }

    private void RaiseAllWidths()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DateWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeWidth)));
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updating) return;
        if (e.PropertyName != nameof(DetailsColumn.Width)) return;
        if (sender is not DetailsColumn col) return;
        var widthProp = col.Key switch
        {
            "Name" => nameof(NameWidth),
            "Date" => nameof(DateWidth),
            "Type" => nameof(TypeWidth),
            "Size" => nameof(SizeWidth),
            _      => null,
        };
        if (widthProp is not null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(widthProp));
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_updating) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DateOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeOrder)));
    }

    private int OrderOf(string key)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].Key == key) return i;
        return 0;
    }

    public int NameOrder => OrderOf("Name");
    public int DateOrder => OrderOf("Date");
    public int TypeOrder => OrderOf("Type");
    public int SizeOrder => OrderOf("Size");

    /// <summary>Gets the column with the given key, or throws if not found.</summary>
    public DetailsColumn this[string key] =>
        Columns.First(c => c.Key == key);

    // ── Legacy scalar properties kept for any existing x:Bind in DataTemplates ──

    public double NameWidth
    {
        get => this["Name"].Width;
        set => this["Name"].Width = value;
    }

    public double DateWidth
    {
        get => this["Date"].Width;
        set => this["Date"].Width = value;
    }

    public double TypeWidth
    {
        get => this["Type"].Width;
        set => this["Type"].Width = value;
    }

    public double SizeWidth
    {
        get => this["Size"].Width;
        set => this["Size"].Width = value;
    }
}
