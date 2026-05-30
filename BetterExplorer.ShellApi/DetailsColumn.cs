using System;
using System.ComponentModel;

namespace BetterExplorer.ShellApi;

/// <summary>
/// Describes a single column in the Details view.
/// </summary>
public sealed class DetailsColumn : INotifyPropertyChanged
{
    // internal so DetailsColumnSettings.ApplyColumns can bulk-set widths without
    // firing PropertyChanged for each column during a batch update.
    internal double _width;
    private string _sortIndicator = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stable string key used for sorting and gripper identity (e.g. "Name", "Date").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Localizable display header text.</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>Minimum resizable width in pixels.</summary>
    public double MinWidth { get; set; } = 40;

    /// <summary>Current column width; clamped to <see cref="MinWidth"/>.</summary>
    public double Width
    {
        get => _width;
        set
        {
            value = Math.Max(MinWidth, value);
            if (_width == value) return;
            _width = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
        }
    }

    /// <summary>Fires PropertyChanged for Width after a batch backing-field update.</summary>
    internal void RaiseWidth() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));

    /// <summary>Sort-direction indicator text (" ▲" / " ▼" / "").</summary>
    public string SortIndicator
    {
        get => _sortIndicator;
        set
        {
            if (_sortIndicator == value) return;
            _sortIndicator = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SortIndicator)));
        }
    }
}
