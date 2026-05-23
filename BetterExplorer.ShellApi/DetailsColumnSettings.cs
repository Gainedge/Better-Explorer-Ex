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

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
