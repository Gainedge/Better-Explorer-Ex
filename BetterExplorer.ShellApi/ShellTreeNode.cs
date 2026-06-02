using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BetterExplorer.ShellApi;

/// <summary>
/// View-model for a single node in the shell navigation tree.
/// Supports lazy child loading via a sentinel dummy child.
/// </summary>
public sealed class ShellTreeNode : INotifyPropertyChanged
{
    // ── Sentinel used as a placeholder child to make the expander arrow appear ──
    public static readonly ShellTreeNode Dummy = new() { Name = string.Empty };

    private string _name = string.Empty;
    private WriteableBitmap? _icon;
    private bool _isExpanded;
    private bool _isLoading;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>True while async children are being fetched (shows a spinner).</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingVisibility)); }
    }

    /// <summary>Collapsed when not loading; used by the XAML template ProgressRing.</summary>
    public Microsoft.UI.Xaml.Visibility LoadingVisibility =>
        _isLoading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Absolute file-system path. Null for virtual nodes (Quick Access root, Network).</summary>
    public string? FullPath { get; set; }

    /// <summary>True for the Quick Access virtual root (no navigate on click).</summary>
    public bool IsVirtual { get; set; }

    /// <summary>True for top-level section headers (Quick Access, This PC, Network).</summary>
    public bool IsGroupHeader { get; set; }

    /// <summary>
    /// True for network server/workgroup nodes that expand to shares but are not
    /// navigable themselves (similar to IsVirtual, but for the network branch).
    /// </summary>
    public bool IsNetworkContainer { get; set; }

    /// <summary>
    /// True for the Explorer-style category nodes under the Network root
    /// (e.g. "Computers", "Media devices", "Infrastructure").
    /// When expanded, the tree uses <see cref="NetworkGroupItems"/> directly
    /// rather than making another network call.
    /// </summary>
    public bool IsNetworkCategoryGroup { get; set; }

    /// <summary>
    /// Pre-fetched items belonging to this category group.
    /// Populated when the Network root is first enumerated so group expansion
    /// is instant and avoids a redundant shell/WNet call.
    /// </summary>
    public List<NativeShell.NetworkResource>? NetworkGroupItems { get; set; }

    /// <summary>
    /// Set on virtual root nodes so the host can navigate via IShellFolder
    /// instead of a plain file-system path.
    /// </summary>
    public Guid? KnownFolderGuid { get; set; }

    /// <summary>True when this node represents the FTP Sites virtual root.</summary>
    public bool IsFtpRoot { get; set; }

    /// <summary>True when this node represents a single saved FTP/SFTP/SCP site.</summary>
    public bool IsFtpSite { get; set; }

    /// <summary>
    /// Arbitrary tag payload.  For FTP site nodes this holds the
    /// <c>BetterExplorer.Controls.FtpSiteEntry</c> instance without
    /// introducing a circular project reference.
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>True when this node represents a folder that can be navigated.</summary>
    public bool IsFolder { get; set; }

    /// <summary>
    /// Extra margin applied to this node's TreeViewItem container.
    /// Used to add visual spacing above group-header root nodes.
    /// </summary>
    public Thickness TopMargin { get; set; }

    /// <summary>16×16 shell type icon; null until loaded asynchronously.</summary>
    public WriteableBitmap? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public ObservableCollection<ShellTreeNode> Children { get; } = [];

    /// <summary>True when this node has at least one child (drives chevron visibility).</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Visibility for the expand chevron — Visible when the node has children.</summary>
    public Microsoft.UI.Xaml.Visibility ChevronVisibility =>
        Children.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>True when Children contains only the Dummy sentinel.</summary>
    public bool HasDummyChild =>
        Children.Count == 1 && ReferenceEquals(Children[0], Dummy);

    public ShellTreeNode()
    {
        Children.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(ChevronVisibility));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
