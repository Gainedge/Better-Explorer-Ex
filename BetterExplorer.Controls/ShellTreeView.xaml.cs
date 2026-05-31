using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterExplorer.ShellApi;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace BetterExplorer.Controls;

public sealed partial class ShellTreeView : UserControl
{
    // ── Public API ────────────────────────────────────────────────────────────

    public static readonly DependencyProperty ShowHiddenFoldersProperty =
        DependencyProperty.Register(
            nameof(ShowHiddenFolders), typeof(bool), typeof(ShellTreeView),
            new PropertyMetadata(false));

    /// <summary>When true, hidden subfolders are visible in the navigation tree.</summary>
    public bool ShowHiddenFolders {
        get => (bool)GetValue(ShowHiddenFoldersProperty);
        set => SetValue(ShowHiddenFoldersProperty, value);
    }

    /// <summary>Raised when the user selects a navigable folder node.</summary>
    public event EventHandler<string>? FolderSelected;

    /// <summary>Raised when the user selects a virtual shell folder (e.g. This PC).
    /// The host should call <see cref="ShellListView.NavigateToKnownFolder"/> with this GUID.</summary>
    public event EventHandler<Guid>? KnownFolderSelected;

    /// <summary>Root nodes bound to the TreeView via ItemsSource.</summary>
    public ObservableCollection<ShellTreeNode> Roots { get; } = [];

    // ── Icon cache (instance-level — WriteableBitmap is tied to a XamlRoot) ──
    private readonly Dictionary<string, WriteableBitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _iconCacheLock = new();

    private double _iconScale = 1.0;
    private bool   _rootsReady;
    private string? _pendingSyncPath;
    private Task?  _librariesLoadTask;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ShellTreeView()
    {
        InitializeComponent();
        NavTree.ItemsSource = Roots;
        NavTree.Expanding  += NavTree_Expanding;
        Loaded += async (_, _) =>
        {
            _iconScale = XamlRoot?.RasterizationScale ?? 1.0;
            await PopulateRootsAsync();
            _rootsReady = true;
            if (_pendingSyncPath is not null)
            {
                var p = _pendingSyncPath;
                _pendingSyncPath = null;
                SyncToPath(p);
            }
        };
    }

    // ── x:Bind helper converters ──────────────────────────────────────────────

    public static Visibility NullToVisible(object? v)    => v == null  ? Visibility.Visible   : Visibility.Collapsed;
    public static Visibility NotNullToVisible(object? v) => v != null  ? Visibility.Visible   : Visibility.Collapsed;

    // ── Root population ───────────────────────────────────────────────────────

    private async Task PopulateRootsAsync()
    {
        var ct = CancellationToken.None;
        var iconSize = (uint)Math.Ceiling(16 * _iconScale);

        // ── Quick Access ──────────────────────────────────────────────────────
        var qa = new ShellTreeNode
        {
            Name          = "Quick Access",
            FullPath      = null,
            IsVirtual     = true,
            IsGroupHeader = true,
            IsFolder      = false,
        };
        Roots.Add(qa);
        _ = LoadChildrenAsync(qa, ct);
        LoadKnownFolderIcon(qa, NativeShell.FOLDERID_QuickAccess, "::qa", iconSize);

        // ── OneDrive ──────────────────────────────────────────────────────────
        var oneDrivePath = Environment.GetEnvironmentVariable("OneDriveConsumer")
                        ?? Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrivePath) && Directory.Exists(oneDrivePath))
        {
            var od = new ShellTreeNode
            {
                Name          = Path.GetFileName(oneDrivePath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "OneDrive",
                FullPath      = oneDrivePath,
                IsVirtual     = false,
                IsGroupHeader = true,
                IsFolder      = true,
                TopMargin     = new Thickness(0, 8, 0, 0),
            };
            od.Children.Add(ShellTreeNode.Dummy);
            Roots.Add(od);
            LoadNodeIcon(od, oneDrivePath, iconSize);
        }

        // ── This PC ───────────────────────────────────────────────────────────
        var pc = new ShellTreeNode
        {
            Name             = "This PC",
            FullPath         = null,
            IsVirtual        = true,
            IsGroupHeader    = true,
            IsFolder         = false,
            KnownFolderGuid  = NativeShell.FOLDERID_ComputerFolder,
            TopMargin        = new Thickness(0, 8, 0, 0),
        };
        Roots.Add(pc);
        LoadIDListKnownFolderIcon(pc, NativeShell.FOLDERID_ComputerFolder, "::pc", iconSize);
        // Await drives so that drive nodes exist before SyncToPath runs.
        await LoadDrivesAsync(pc, iconSize, ct);

        // ── Libraries ─────────────────────────────────────────────────────────
        var lib = new ShellTreeNode
        {
            Name             = "Libraries",
            FullPath         = null,
            IsVirtual        = true,
            IsGroupHeader    = true,
            IsFolder         = false,
            KnownFolderGuid  = NativeShell.FOLDERID_Libraries,
            TopMargin        = new Thickness(0, 8, 0, 0),
        };
        Roots.Add(lib);
        LoadIDListKnownFolderIcon(lib, NativeShell.FOLDERID_Libraries, "::lib", iconSize);
        _librariesLoadTask = LoadLibrariesAsync(lib, iconSize, ct);

        // ── Network ───────────────────────────────────────────────────────────
        var net = new ShellTreeNode
        {
            Name          = "Network",
            FullPath      = null,
            IsVirtual     = true,
            IsGroupHeader = true,
            IsFolder      = false,
            TopMargin     = new Thickness(0, 8, 0, 0),
        };
        net.Children.Add(ShellTreeNode.Dummy);
        Roots.Add(net);
        LoadIDListKnownFolderIcon(net, NativeShell.FOLDERID_NetworkFolder, "::net", iconSize);
    }

    // ── Quick Access children ─────────────────────────────────────────────────

    private async Task LoadChildrenAsync(ShellTreeNode parent, CancellationToken ct)
    {
        var iconSize = (uint)Math.Ceiling(16 * _iconScale);
        var children = await Task.Run(NativeShell.EnumerateQuickAccessFolders, ct);
        foreach (var (name, path) in children)
        {
            var node = new ShellTreeNode
            {
                Name     = name,
                FullPath = path,
                IsFolder = true,
            };
            if (HasSubfolders(path))
                node.Children.Add(ShellTreeNode.Dummy);
            parent.Children.Add(node);
            LoadNodeIcon(node, path, iconSize);
        }
    }

    // ── This PC drives ────────────────────────────────────────────────────────

    private async Task LoadDrivesAsync(ShellTreeNode parent, uint iconSize, CancellationToken ct)
    {
        var drives = await Task.Run(() =>
        {
            var list = new System.Collections.Generic.List<(string Label, string Root)>();
            foreach (var di in System.IO.DriveInfo.GetDrives())
            {
                if (!di.IsReady && di.DriveType != System.IO.DriveType.Fixed) continue;
                var root = di.RootDirectory.FullName;
                // Use the shell display name (e.g. "Local Disk (C:)") which matches
                // what Explorer and the breadcrumb bar show.
                var label = NativeShell.GetShellDisplayName(root);
                if (string.IsNullOrEmpty(label))
                    label = root.TrimEnd('\\');
                list.Add((label, root));
            }
            return list;
        }, ct);

        foreach (var (label, root) in drives)
        {
            var node = new ShellTreeNode { Name = label, FullPath = root, IsFolder = true };
            node.Children.Add(ShellTreeNode.Dummy);
            parent.Children.Add(node);
            LoadNodeIcon(node, root, iconSize);
        }
    }

    // ── Libraries ─────────────────────────────────────────────────────────────

    private async Task LoadLibrariesAsync(ShellTreeNode parent, uint iconSize, CancellationToken ct)
    {
        var librariesPath = NativeShell.SHGetKnownFolderPath(NativeShell.FOLDERID_Libraries);
        if (librariesPath == null || !Directory.Exists(librariesPath)) return;

        var files = await Task.Run(() =>
            System.IO.Directory.EnumerateFiles(librariesPath, "*.library-ms").ToList(), ct);

        foreach (var libFile in files)
        {
            var displayName = Path.GetFileNameWithoutExtension(libFile);
            var targetPath  = NativeShell.ResolveLibraryDefaultPath(libFile);

            var node = new ShellTreeNode
            {
                Name     = displayName,
                FullPath = targetPath,
                IsFolder = targetPath != null,
                IsVirtual= targetPath == null,
            };
            if (targetPath != null && HasSubfolders(targetPath))
                node.Children.Add(ShellTreeNode.Dummy);
            parent.Children.Add(node);
            LoadNodeIcon(node, libFile, iconSize);
        }
    }

    // ── Lazy expand ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="path"/> contains at least one non-reparse-point
    /// subdirectory that passes the current hidden-folder filter.
    /// </summary>
    private bool HasSubfolders(string path)
    {
        bool showHidden = ShowHiddenFolders;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                var attrs = File.GetAttributes(dir);
                if (!showHidden && (attrs & System.IO.FileAttributes.Hidden) != 0) continue;
                if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    private void NavTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is not ShellTreeNode node) return;
        if (!node.HasDummyChild) return;
        node.Children.Clear();
        _ = LoadSubfoldersAsync(node, CancellationToken.None);
    }
    private async Task LoadSubfoldersAsync(ShellTreeNode parent, CancellationToken ct)
    {
        if (parent.FullPath == null) return;
        var iconSize = (uint)Math.Ceiling(16 * _iconScale);

        bool showHidden = ShowHiddenFolders;
        var subfolders = await Task.Run(() =>
        {
            var list = new System.Collections.Generic.List<(string Name, string Path)>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(parent.FullPath))
                {
                    var attrs = File.GetAttributes(dir);
                    if (!showHidden && (attrs & System.IO.FileAttributes.Hidden) != 0) continue;
                    if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                    list.Add((Path.GetFileName(dir), dir));
                }
            }
            catch { }
            return list;
        }, ct);

        if (ct.IsCancellationRequested) return;
        if (subfolders.Count == 0) return;

        foreach (var (name, path) in subfolders)
        {
            var node = new ShellTreeNode { Name = name, FullPath = path, IsFolder = true };
            if (HasSubfolders(path))
                node.Children.Add(ShellTreeNode.Dummy);
            parent.Children.Add(node);
            LoadNodeIcon(node, path, iconSize);
        }
    }

    // ── Item invoked ──────────────────────────────────────────────────────────

    private void NavTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (_suppressItemInvoked) return;
        if (args.InvokedItem is not ShellTreeNode node) return;

        if (node.KnownFolderGuid.HasValue)
            KnownFolderSelected?.Invoke(this, node.KnownFolderGuid.Value);
        else if (!node.IsVirtual && node.FullPath is string path)
            FolderSelected?.Invoke(this, path);
    }

    // ── External sync ─────────────────────────────────────────────────────────

    // Set to true while we are programmatically changing the selection so that
    // NavTree_ItemInvoked does not fire a second navigation.
    private bool _suppressItemInvoked;

    /// <summary>
    /// Called by the host when the list-view navigates so the tree stays in sync.
    /// Expands ancestor nodes lazily as required, then selects the matching node.
    /// </summary>
    public async void SyncToPath(string path)
    {
        path = path.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(path)) return;

        if (!_rootsReady)
        {
            _pendingSyncPath = path;
            return;
        }

        // Virtual known-folder path e.g. ::{20D04FE0-3AEA-1069-A2D8-08002B30309D}
        // These paths have no filesystem ancestors — match directly by KnownFolderGuid.
        if (path.StartsWith("::{", StringComparison.Ordinal))
        {
            var guidStr = path.Substring(2); // strip leading ::
            if (Guid.TryParse(guidStr, out var knownGuid))
            {
                // Bare known-folder GUID — select the matching root node directly.
                var kfNode = FindNodeByKnownFolderGuid(Roots, knownGuid);
                if (kfNode != null)
                {
                    _suppressItemInvoked = true;
                    NavTree.SelectedItem = kfNode;
                    _suppressItemInvoked = false;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        ScrollTreeToNode(kfNode));
                }
                return;
            }

            // Compound virtual path e.g. ::{GUID}\Documents.library-ms
            // Strategy 1: try to resolve to a real filesystem path via the shell.
            var fsResolved = NativeShell.TryGetFileSystemPath(path);

            // If the shell gave us a .library-ms file path, resolve its default
            // save location so we get the actual directory (e.g. C:\Users\Joe\Documents).
            if (!string.IsNullOrEmpty(fsResolved)
                && fsResolved.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase))
                fsResolved = NativeShell.ResolveLibraryDefaultPath(fsResolved);

            if (!string.IsNullOrEmpty(fsResolved))
            {
                // Strategy 2: search all root children directly for a node whose
                // FullPath matches the resolved path. Library nodes live directly
                // under the Libraries root without intermediate C:\, C:\Users\... nodes,
                // so the normal ancestor walk would never find them.
                var target = fsResolved.TrimEnd('\\', '/');
                foreach (var root in Roots)
                {
                    var direct = FindNodeByFullPath(root.Children, target);
                    if (direct != null)
                    {
                        root.IsExpanded = true;
                        _suppressItemInvoked = true;
                        NavTree.SelectedItem = direct;
                        _suppressItemInvoked = false;
                        DispatcherQueue.TryEnqueue(
                            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                            () => ScrollTreeToNode(direct));
                        return;
                    }
                }

                // Strategy 3: the resolved path may be nested deeper (e.g. inside a
                // library folder); fall through to the normal ancestor walk.
                SyncToPath(fsResolved);
                return;
            }

            // Strategy 4: path contains .library-ms — match by library name under
            // the Libraries root. LoadLibrariesAsync stores Name = GetFileNameWithoutExtension,
            // so "Documents.library-ms" matches the node whose Name == "Documents".
            var lastSegment = path.Split('\\', '/').LastOrDefault() ?? string.Empty;
            if (lastSegment.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase))
            {
                var libName = Path.GetFileNameWithoutExtension(lastSegment);
                var libRoot = FindNodeByKnownFolderGuid(Roots, NativeShell.FOLDERID_Libraries);
                if (libRoot != null)
                {
                    // Wait for LoadLibrariesAsync if it hasn't finished yet.
                    if (_librariesLoadTask != null)
                        await _librariesLoadTask;

                    var libNode = libRoot.Children.FirstOrDefault(n =>
                        string.Equals(n.Name, libName, StringComparison.OrdinalIgnoreCase));
                    if (libNode != null)
                    {
                        libRoot.IsExpanded = true;
                        _suppressItemInvoked = true;
                        NavTree.SelectedItem = libNode;
                        _suppressItemInvoked = false;
                        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                            ScrollTreeToNode(libNode));
                        return;
                    }
                }
            }

            // Strategy 5: no filesystem representation at all — try a direct
            // virtual-path match against any already-loaded tree node.
            var virtualNode = FindNodeByFullPath(Roots, path);
            if (virtualNode != null)
            {
                _suppressItemInvoked = true;
                NavTree.SelectedItem = virtualNode;
                _suppressItemInvoked = false;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    ScrollTreeToNode(virtualNode));
            }
            return;
        }

        // Build the ordered list of path components we need to walk, e.g.
        // "C:\Users\Joe\Documents" → ["C:\", "C:\Users", "C:\Users\Joe", "C:\Users\Joe\Documents"]
        var segments = GetPathAncestors(path);

        foreach (var root in Roots)
        {
            var found = await TryExpandToPathAsync(root, segments, depth: 0);
            if (found != null)
            {
                _suppressItemInvoked = true;
                NavTree.SelectedItem = found;
                _suppressItemInvoked = false;

                // Scroll the tree to the selected node.
                // TransformToVisual is unreliable for virtualised rows, so we
                // count the flat visible row index and multiply by row height.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    ScrollTreeToNode(found));

                return;
            }
        }
    }

    /// <summary>
    /// Recursively searches <paramref name="nodes"/> (and their loaded children)
    /// for a node whose <see cref="ShellTreeNode.KnownFolderGuid"/> matches.
    /// </summary>
    private static ShellTreeNode? FindNodeByKnownFolderGuid(
        IEnumerable<ShellTreeNode> nodes, Guid guid)
    {
        foreach (var node in nodes)
        {
            if (node.KnownFolderGuid == guid) return node;
            var found = FindNodeByKnownFolderGuid(node.Children, guid);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Recursively searches <paramref name="nodes"/> for a node whose
    /// <see cref="ShellTreeNode.FullPath"/> matches <paramref name="path"/> exactly
    /// (case-insensitive, trailing separators ignored). Used to locate virtual-path
    /// nodes and library nodes that have no GUID.
    /// </summary>
    private static ShellTreeNode? FindNodeByFullPath(
        IEnumerable<ShellTreeNode> nodes, string path)
    {
        var target = path.TrimEnd('\\', '/');
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullPath?.TrimEnd('\\', '/'), target, StringComparison.OrdinalIgnoreCase))
                return node;
            var found = FindNodeByFullPath(node.Children, target);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Returns the chain of paths leading up to (and including) <paramref name="path"/>,
    /// ordered from shallowest to deepest.
    /// </summary>
    private static List<string> GetPathAncestors(string path)
    {
        var result = new List<string>();
        var current = path;
        while (true)
        {
            result.Insert(0, current.TrimEnd('\\', '/'));
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        return result;
    }

    /// <summary>
    /// Recursively walks the in-memory tree, expanding lazy nodes as needed,
    /// until the node whose <see cref="ShellTreeNode.FullPath"/> matches the
    /// deepest segment is found. Returns the matching node, or null.
    /// </summary>
    private async Task<ShellTreeNode?> TryExpandToPathAsync(
        ShellTreeNode node, List<string> segments, int depth)
    {
        var nodePath = node.FullPath?.TrimEnd('\\', '/');
        if (nodePath == null)
        {
            // Virtual/group-header node (Quick Access, This PC, Libraries, Network):
            // load its children if still holding a dummy, then descend without
            // advancing depth (virtual nodes are not part of the path).
            if (node.HasDummyChild)
            {
                node.Children.Clear();
                await LoadSubfoldersAsync(node, CancellationToken.None);
            }

            foreach (var child in node.Children.ToList())
            {
                var found = await TryExpandToPathAsync(child, segments, depth);
                if (found != null)
                {
                    // Expand the group-header so the selected child is visible.
                    node.IsExpanded = true;
                    return found;
                }
            }
            return null;
        }

        // Check if this node is one of the ancestors we need.
        bool isAncestorOrTarget = depth < segments.Count &&
            string.Equals(nodePath, segments[depth], StringComparison.OrdinalIgnoreCase);

        if (!isAncestorOrTarget) return null;

        // Exact match — this is the target node.
        if (depth == segments.Count - 1) return node;

        // Ancestor match — make sure children are loaded, then recurse.
        if (node.HasDummyChild)
        {
            node.Children.Clear();
            await LoadSubfoldersAsync(node, CancellationToken.None);
        }

        // Expand the node in the TreeView so its children are visible.
        node.IsExpanded = true;

        foreach (var child in node.Children.ToList())
        {
            var found = await TryExpandToPathAsync(child, segments, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    // ── Shell-change notification API (called by ExplorerBrowser) ────────────

    /// <summary>
    /// Called when a new drive has been attached or a removable volume has been mounted.
    /// Adds the drive node under "This PC" if it is not already present.
    /// </summary>
    public void NotifyDriveAdded(string driveRoot)
    {
        driveRoot = driveRoot.TrimEnd('\\') + '\\';
        var pc = FindNodeByKnownFolderGuid(Roots, NativeShell.FOLDERID_ComputerFolder);
        if (pc == null) return;

        // If the node is already there (spurious notification), ignore.
        foreach (var child in pc.Children)
            if (string.Equals(child.FullPath?.TrimEnd('\\') + '\\', driveRoot, StringComparison.OrdinalIgnoreCase))
                return;

        var label = NativeShell.GetShellDisplayName(driveRoot);
        if (string.IsNullOrEmpty(label)) label = driveRoot.TrimEnd('\\');

        var iconSize = (uint)Math.Ceiling(16 * _iconScale);
        var node = new ShellTreeNode { Name = label, FullPath = driveRoot, IsFolder = true };
        node.Children.Add(ShellTreeNode.Dummy);
        pc.Children.Add(node);
        LoadNodeIcon(node, driveRoot, iconSize);
    }

    /// <summary>
    /// Called when a drive has been removed or a removable volume unmounted.
    /// Removes the matching drive node from "This PC".
    /// </summary>
    public void NotifyDriveRemoved(string driveRoot)
    {
        driveRoot = driveRoot.TrimEnd('\\') + '\\';
        var pc = FindNodeByKnownFolderGuid(Roots, NativeShell.FOLDERID_ComputerFolder);
        if (pc == null) return;

        var toRemove = pc.Children.FirstOrDefault(n =>
            string.Equals(n.FullPath?.TrimEnd('\\') + '\\', driveRoot, StringComparison.OrdinalIgnoreCase));
        if (toRemove != null)
            pc.Children.Remove(toRemove);
    }

    /// <summary>
    /// Called when a new folder has been created on the filesystem.
    /// If the parent folder is already expanded in the tree the new node is added.
    /// </summary>
    public void NotifyFolderCreated(string folderPath)
    {
        var parentPath = Path.GetDirectoryName(folderPath);
        if (parentPath == null) return;

        var parentNode = FindNodeByPath(Roots, parentPath);
        if (parentNode == null || parentNode.HasDummyChild) return; // not yet expanded – the expand will pick it up

        // Guard: do not add duplicates.
        foreach (var existing in parentNode.Children)
            if (string.Equals(existing.FullPath, folderPath, StringComparison.OrdinalIgnoreCase))
                return;

        var iconSize = (uint)Math.Ceiling(16 * _iconScale);
        var node = new ShellTreeNode
        {
            Name     = Path.GetFileName(folderPath),
            FullPath = folderPath,
            IsFolder = true,
        };
        if (HasSubfolders(folderPath))
            node.Children.Add(ShellTreeNode.Dummy);

        // Insert in alphabetical order.
        int insertAt = 0;
        for (int i = 0; i < parentNode.Children.Count; i++)
        {
            if (string.Compare(parentNode.Children[i].Name, node.Name, StringComparison.OrdinalIgnoreCase) <= 0)
                insertAt = i + 1;
            else
                break;
        }
        parentNode.Children.Insert(insertAt, node);
        LoadNodeIcon(node, folderPath, iconSize);
    }

    /// <summary>
    /// Called when a folder has been deleted from the filesystem.
    /// Removes the matching node (and any descendant selection state).
    /// </summary>
    public void NotifyFolderDeleted(string folderPath)
    {
        RemoveNodeByPath(Roots, folderPath);
    }

    /// <summary>
    /// Called when a folder has been renamed on the filesystem.
    /// Updates the node name and <see cref="ShellTreeNode.FullPath"/> in-place
    /// and recursively fixes all descendant paths.
    /// </summary>
    public void NotifyFolderRenamed(string oldPath, string newPath)
    {
        var node = FindNodeByPath(Roots, oldPath);
        if (node == null) return;

        node.Name     = Path.GetFileName(newPath);
        node.FullPath = newPath;
        FixDescendantPaths(node, oldPath, newPath);
    }

    // ── Tree search / mutation helpers ───────────────────────────────────────

    private static ShellTreeNode? FindNodeByPath(IEnumerable<ShellTreeNode> nodes, string path)
    {
        path = path.TrimEnd('\\', '/');
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, ShellTreeNode.Dummy)) continue;
            var nodePath = node.FullPath?.TrimEnd('\\', '/');
            if (string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase))
                return node;
            if (node.IsExpanded || (nodePath != null && path.StartsWith(nodePath, StringComparison.OrdinalIgnoreCase)))
            {
                var found = FindNodeByPath(node.Children, path);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static bool RemoveNodeByPath(ObservableCollection<ShellTreeNode> children, string path)
    {
        path = path.TrimEnd('\\', '/');
        for (int i = 0; i < children.Count; i++)
        {
            var node = children[i];
            if (ReferenceEquals(node, ShellTreeNode.Dummy)) continue;
            var nodePath = node.FullPath?.TrimEnd('\\', '/');
            if (string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase))
            {
                children.RemoveAt(i);
                return true;
            }
            if (nodePath != null && path.StartsWith(nodePath, StringComparison.OrdinalIgnoreCase))
            {
                if (RemoveNodeByPath(node.Children, path)) return true;
            }
        }
        return false;
    }

    private static void FixDescendantPaths(ShellTreeNode node, string oldBase, string newBase)
    {
        foreach (var child in node.Children)
        {
            if (ReferenceEquals(child, ShellTreeNode.Dummy)) continue;
            if (child.FullPath != null && child.FullPath.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase))
            {
                child.FullPath = newBase + child.FullPath.Substring(oldBase.Length);
                FixDescendantPaths(child, oldBase, newBase);
            }
        }
    }

    // ── Visual-tree helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Scrolls the tree's internal ScrollViewer so that <paramref name="target"/> is visible.
    /// We accumulate a pixel offset by walking the expanded tree (including per-node
    /// TopMargin gaps) instead of relying on TransformToVisual, which is unreliable
    /// for virtualised rows.
    /// </summary>
    private void ScrollTreeToNode(ShellTreeNode target)
    {
        var sv = FindDescendant<ScrollViewer>(NavTree);
        if (sv == null) return;

        double rowHeight = MeasureRowHeight();
        if (rowHeight <= 0) return;

        double offset = 0;
        bool   found  = false;
        foreach (var root in Roots)
            if (AccumulateOffset(root, target, rowHeight, ref offset, ref found))
                break;

        if (!found) return;

        double itemBottom = offset + rowHeight;
        double viewTop    = sv.VerticalOffset;
        double viewBottom = viewTop + sv.ViewportHeight;

        if (offset < viewTop)
            sv.ChangeView(null, offset - 4, null, true);
        else if (itemBottom > viewBottom)
            sv.ChangeView(null, itemBottom - sv.ViewportHeight + 4, null, true);
    }

    /// <summary>
    /// Recursively walks the visible tree, accumulating the pixel Y offset for each
    /// node (row height + any TopMargin gap). Sets <paramref name="offset"/> to the
    /// top of <paramref name="target"/> when found.
    /// </summary>
    private static bool AccumulateOffset(ShellTreeNode node, ShellTreeNode target,
                                         double rowHeight, ref double offset, ref bool found)
    {
        if (ReferenceEquals(node, ShellTreeNode.Dummy)) return false;

        // Account for any top gap (section separators).
        offset += node.TopMargin.Top;

        if (ReferenceEquals(node, target)) { found = true; return true; }

        offset += rowHeight;

        if (node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                if (AccumulateOffset(child, target, rowHeight, ref offset, ref found))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the actual rendered height of the first realized TreeViewItem,
    /// or a sensible default of 28 px.
    /// </summary>
    private double MeasureRowHeight()
    {
        var tvi = FindDescendant<TreeViewItem>(NavTree);
        if (tvi != null && tvi.ActualHeight > 0) return tvi.ActualHeight;
        return 28;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Walks the visual tree under <paramref name="parent"/> and returns the first
    /// <see cref="TreeViewItem"/> whose <c>DataContext</c> is <paramref name="node"/>.
    /// </summary>
    private static TreeViewItem? FindTreeViewItemForNode(DependencyObject parent, ShellTreeNode node)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TreeViewItem tvi && tvi.DataContext == node)
                return tvi;
            var found = FindTreeViewItemForNode(child, node);
            if (found != null) return found;
        }
        return null;
    }

    // ── Icon loading helpers ──────────────────────────────────────────────────

    private void LoadNodeIcon(ShellTreeNode node, string path, uint iconSize)
    {
        var cacheKey = $"{path.TrimEnd('\\').ToLowerInvariant()}@{iconSize}";
        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(cacheKey, out var cached)) { node.Icon = cached; return; }
        }
        var hbm = NativeShell.TryGetShellHBitmap(path, iconSize, NativeShell.SIIGBF.IconOnly);
        if (hbm == IntPtr.Zero) return;
        WriteableBitmap? wb;
        try   { wb = NativeShell.HBitmapToWriteableBitmap(hbm); }
        finally { NativeShell.DeleteObject(hbm); }
        if (wb == null) return;
        lock (_iconCacheLock) _iconCache.TryAdd(cacheKey, wb);
        node.Icon = wb;
    }

    private void LoadKnownFolderIcon(ShellTreeNode node, Guid folderId, string cacheKey, uint iconSize)
    {
        var sizedKey = $"{cacheKey}@{iconSize}";
        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(sizedKey, out var cached)) { node.Icon = cached; return; }
        }
        var shellPath = $"shell:::{{{folderId}}}";
        var hbm = NativeShell.TryGetShellHBitmap(shellPath, iconSize, NativeShell.SIIGBF.IconOnly);
        if (hbm == IntPtr.Zero) return;
        WriteableBitmap? wb;
        try   { wb = NativeShell.HBitmapToWriteableBitmap(hbm); }
        finally { NativeShell.DeleteObject(hbm); }
        if (wb == null) return;
        lock (_iconCacheLock) _iconCache.TryAdd(sizedKey, wb);
        node.Icon = wb;
    }

    private void LoadIDListKnownFolderIcon(ShellTreeNode node, Guid folderId, string cacheKey, uint iconSize)
    {
        var sizedKey = $"{cacheKey}@{iconSize}";
        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(sizedKey, out var cached)) { node.Icon = cached; return; }
        }
        var hbm = NativeShell.TryGetIDListKnownFolderHBitmap(folderId, iconSize);
        if (hbm == IntPtr.Zero) return;
        WriteableBitmap? wb;
        try   { wb = NativeShell.HBitmapToWriteableBitmap(hbm); }
        finally { NativeShell.DeleteObject(hbm); }
        if (wb == null) return;
        lock (_iconCacheLock) _iconCache.TryAdd(sizedKey, wb);
        node.Icon = wb;
    }
}
