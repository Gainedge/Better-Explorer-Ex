using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace BetterExplorer.ShellApi;

public sealed class ShellItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _displayName = string.Empty;
    private string _fullPath = string.Empty;
    private string _itemType = string.Empty;
    private string _size = string.Empty;
    private long _sizeBytes;
    private DateTime _dateModified;
    private bool _isFolder;
    private bool _isHidden;
    private ImageSource? _icon;
    private bool _hasRealThumbnail;
    private bool _isSelected;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The name shown in the UI.  Equals <see cref="Name"/> when file extensions are visible,
    /// or the name without extension when they are hidden.  Set by the host control.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    public string FullPath
    {
        get => _fullPath;
        set { _fullPath = value; OnPropertyChanged(); }
    }

    public string ItemType
    {
        get => _itemType;
        set { _itemType = value; OnPropertyChanged(); }
    }

    public string Size
    {
        get => _size;
        set { _size = value; OnPropertyChanged(); }
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set { _sizeBytes = value; OnPropertyChanged(); }
    }

    public DateTime DateModified
    {
        get => _dateModified;
        set { _dateModified = value; OnPropertyChanged(); OnPropertyChanged(nameof(DateModifiedString)); }
    }

    public string DateModifiedString => _dateModified == default ? string.Empty : _dateModified.ToString("g");

    public bool IsFolder
    {
        get => _isFolder;
        set { _isFolder = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconGlyph)); }
    }

    public bool IsHidden
    {
        get => _isHidden;
        set { _isHidden = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconOpacity)); }
    }

    private bool _isCut;

    /// <summary>True while this item is on the clipboard as a cut (move) operation. Dims the icon like Explorer.</summary>
    public bool IsCut
    {
        get => _isCut;
        set { _isCut = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconOpacity)); }
    }

    /// <summary>Ghost opacity: cut and hidden items are dimmed to 40%, matching Explorer.</summary>
    public double IconOpacity => (_isHidden || _isCut) ? 0.4 : 1.0;

    public string IconGlyph => _isFolder ? "\uE8B7" : "\uE8A5";

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconFallbackVisibility)); }
    }

    /// <summary>Visible when no icon has been loaded yet — lets the template show a FontIcon placeholder.</summary>
    public Microsoft.UI.Xaml.Visibility IconFallbackVisibility =>
        _icon == null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>True once a real per-file thumbnail (not just a type icon) has been decoded and set.</summary>
    public bool HasRealThumbnail
    {
        get => _hasRealThumbnail;
        set { _hasRealThumbnail = value; OnPropertyChanged(); }
    }

    /// <summary>Mirrors the ListViewItem selection state; kept in sync via SelectionChanged so Phase 0 can seed the container's IsSelected before it renders.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private bool _isDropTarget;

    /// <summary>True while a drag operation is hovering over this item as a drop target. Used to show the drop-target highlight in the item template.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { _isDropTarget = value; OnPropertyChanged(); }
    }

    private bool _isLabelHidden;
    /// <summary>True while the name-expansion popup is open for this item; hides the in-template label to avoid double text.</summary>
    public bool IsLabelHidden
    {
        get => _isLabelHidden;
        set
        {
            if (_isLabelHidden == value) return;
            _isLabelHidden = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LabelVisibility));
        }
    }

    public Microsoft.UI.Xaml.Visibility LabelVisibility =>
        _isLabelHidden ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private ImageSource? _overlayIcon;

    /// <summary>Small overlay badge rendered on top of the main icon (e.g. shortcut arrow, OneDrive sync, Git status).</summary>
    public ImageSource? OverlayIcon {
        get => _overlayIcon;
        set { _overlayIcon = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverlayIconVisibility)); }
    }

    /// <summary>Visible when a shell overlay icon has been loaded for this item.</summary>
    public Microsoft.UI.Xaml.Visibility OverlayIconVisibility =>
        _overlayIcon == null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Silently clears image references without raising <see cref="PropertyChanged"/>.
    /// Call this just before the owning collection is cleared so WinUI containers
    /// are not asked to re-render items that are about to be thrown away.
    /// </summary>
    public void ClearReferences() {
        _icon = null;
        _overlayIcon = null;
        _isSelected = false;
        _isCut = false;
        _hasRealThumbnail = false;
        _displayName = string.Empty;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
