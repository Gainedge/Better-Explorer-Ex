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

    /// <summary>True for items loaded from an FTP/FTPS/SFTP/SCP session.
    /// Immutable once set — no PropertyChanged needed.</summary>
    public bool IsFtpItem { get; set; }

    /// <summary>True when this item is a shell shortcut (.lnk).</summary>
    public bool IsShortcut { get; set; }

    /// <summary>True when this item is a filesystem junction, symlink, or mount point.</summary>
    public bool IsLinkItem { get; set; }

    /// <summary>True when this item is a compressed archive file that Windows treats as a folder (.zip, .cab, etc.).</summary>
    public bool IsArchive { get; set; }

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

    // ── Drive-space fields (populated for ThisPC items only) ──────────────
    private bool _isDrive;
    private long _driveTotalBytes;
    private long _driveUsedBytes;
    private string _driveGroupType = string.Empty;

    /// <summary>True when this item was enumerated from the Network shell namespace.
    /// Every network device has a unique icon so the icon cache must key it per-item.</summary>
    public bool IsNetworkItem { get; set; }

    /// <summary>True when this item is a drive or removable storage shown in the This PC view.</summary>
    public bool IsDrive {
        get => _isDrive;
        set {
            _isDrive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DriveSpaceVisibility));
        }
    }

    /// <summary>Total capacity of the drive in bytes (0 for non-drives).</summary>
    public long DriveTotalBytes {
        get => _driveTotalBytes;
        set { _driveTotalBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(DriveUsedFraction)); OnPropertyChanged(nameof(DriveSpaceText)); }
    }

    /// <summary>Used space of the drive in bytes (0 for non-drives).</summary>
    public long DriveUsedBytes {
        get => _driveUsedBytes;
        set { _driveUsedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(DriveUsedFraction)); OnPropertyChanged(nameof(DriveSpaceText)); OnPropertyChanged(nameof(DriveBarForeground)); }
    }

    /// <summary>0.0–1.0 fraction used; drives with no capacity return 0.</summary>
    public double DriveUsedFraction =>
        _driveTotalBytes > 0 ? Math.Clamp((double)_driveUsedBytes / _driveTotalBytes, 0.0, 1.0) : 0.0;

    /// <summary>Localised "X GB free of Y GB" string, empty for non-drives.</summary>
    public string DriveSpaceText {
        get {
            if (_driveTotalBytes <= 0) return string.Empty;
            long freeBytes = _driveTotalBytes - _driveUsedBytes;
            return $"{FormatBytes(freeBytes)} free of {FormatBytes(_driveTotalBytes)}";
        }
    }

    /// <summary>Accent colour for the drive bar — red when less than 10 % is free.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush DriveBarForeground =>
        DriveUsedFraction >= 0.9
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);

    /// <summary>Collapsed for non-drive items so the drive-bar row takes no space.</summary>
    public Microsoft.UI.Xaml.Visibility DriveSpaceVisibility =>
        _isDrive && _driveTotalBytes > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Shell drive-type label used for This PC group headers.</summary>
    public string DriveGroupType {
        get => _driveGroupType;
        set { _driveGroupType = value; OnPropertyChanged(); }
    }

    private static string FormatBytes(long bytes) {
        const long KB = 1_024L;
        const long MB = KB * 1_024;
        const long GB = MB * 1_024;
        const long TB = GB * 1_024;
        const long PB = TB * 1_024;
        if (bytes >= PB) return $"{bytes / (double)PB:F2} PB";
        if (bytes >= TB) return $"{bytes / (double)TB:F2} TB";
        if (bytes >= GB) return $"{bytes / (double)GB:F2} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:F0} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:F0} KB";
        return $"{bytes} B";
    }

    // ── Tooltip metadata ──────────────────────────────────────────────────
    private string _imageDimensions = string.Empty;
    private int _rating;
    private ImageSource? _thumbnailLarge;

    /// <summary>Image dimensions string (e.g. "1920 × 1080") for tooltip display. Empty for non-image items.</summary>
    public string ImageDimensions {
        get => _imageDimensions;
        set { _imageDimensions = value; OnPropertyChanged(); }
    }

    /// <summary>Photo rating (0–5) for tooltip display. 0 means no rating.</summary>
    public int Rating {
        get => _rating;
        set { _rating = value; OnPropertyChanged(); }
    }

    /// <summary>Large 1024 px thumbnail used in the tooltip for picture items. Loaded lazily on hover.</summary>
    public ImageSource? ThumbnailLarge {
        get => _thumbnailLarge;
        set { _thumbnailLarge = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThumbnailLargeVisibility)); }
    }

    /// <summary>Visible when a large tooltip thumbnail has been loaded.</summary>
    public Microsoft.UI.Xaml.Visibility ThumbnailLargeVisibility =>
        _thumbnailLarge == null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>True when the item is a picture file (tooltip should show large preview).</summary>
    public bool IsPicture { get; set; }

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
        _thumbnailLarge = null;
        _isSelected = false;
        _isCut = false;
        _hasRealThumbnail = false;
        _displayName = string.Empty;
        _isDrive = false;
        _driveTotalBytes = 0;
        _driveUsedBytes = 0;
        _driveGroupType = string.Empty;
        _imageDimensions = string.Empty;
        _rating = 0;
        IsPicture = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
