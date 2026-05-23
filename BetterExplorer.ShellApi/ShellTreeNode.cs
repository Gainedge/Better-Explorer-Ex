using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>Absolute file-system path. Null for virtual nodes (Quick Access root, Network).</summary>
    public string? FullPath { get; set; }

    /// <summary>True for the Quick Access virtual root (no navigate on click).</summary>
    public bool IsVirtual { get; set; }

    /// <summary>True for top-level section headers (Quick Access, This PC, Network).</summary>
    public bool IsGroupHeader { get; set; }

    /// <summary>
    /// Set on virtual root nodes so the host can navigate via IShellFolder
    /// instead of a plain file-system path.
    /// </summary>
    public Guid? KnownFolderGuid { get; set; }

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
