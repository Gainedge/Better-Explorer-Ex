using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSCP;

namespace BetterExplorer.Controls;

/// <summary>View mode for the FTP browser file list.</summary>
public enum FtpViewMode { Icons, List, Details }

/// <summary>Sort column for the FTP browser.</summary>
public enum FtpSortColumn { Name, Size, Modified, Type }

/// <summary>View-model item for a single remote file or directory.</summary>
public sealed class RemoteFsItem
{
    public string   Name        { get; set; } = string.Empty;
    public bool     IsDirectory { get; set; }
    public long     Size        { get; set; }
    public DateTime Modified    { get; set; }
    public string   Icon        => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string   TypeText    => IsDirectory ? "Folder"
                                  : System.IO.Path.GetExtension(Name) is { Length: > 0 } ext
                                      ? ext.TrimStart('.').ToUpperInvariant() + " File"
                                      : "File";
    public string   SizeText     => IsDirectory ? string.Empty : FormatSize(Size);
    public string   ModifiedText => Modified == default ? string.Empty : Modified.ToString("g");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024           => $"{bytes} B",
        < 1024 * 1024    => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

/// <summary>
/// WinSCP-backed FTP/FTPS/SFTP/SCP browser panel.
/// Connect by calling <see cref="ConnectAsync"/>.
/// </summary>
public sealed partial class FtpBrowserPage : UserControl
{
    public ObservableCollection<RemoteFsItem> RemoteItems { get; } = [];

    private Session?                 _session;
    private FtpSiteEntry?            _site;
    private string                   _currentPath  = "/";
    private CancellationTokenSource  _cts          = new();

    // ── View / sort state ────────────────────────────────────────────────────
    public FtpViewMode   ViewMode      { get; private set; } = FtpViewMode.Details;
    public FtpSortColumn SortColumn    { get; private set; } = FtpSortColumn.Name;
    public bool          SortAscending { get; private set; } = true;
    public string?       GroupColumn   { get; private set; } = null;

    // ── Events ───────────────────────────────────────────────────────────────
    /// <summary>Fired when the user clicks Disconnect so the host can hide this panel.</summary>
    public event EventHandler? Disconnected;

    public FtpBrowserPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => DisconnectSilently();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task ConnectAsync(FtpSiteEntry site)
    {
        _site              = site;
        SiteTitleText.Text = site.DisplayName;
        _currentPath       = string.IsNullOrEmpty(site.RemotePath) ? "/" : site.RemotePath;
        await OpenSessionAndLoadAsync();
    }

    public void ApplyViewMode(FtpViewMode mode)
    {
        ViewMode = mode;
        UpdateViewLayout();
    }

    public void ApplySort(FtpSortColumn column, bool ascending)
    {
        SortColumn    = column;
        SortAscending = ascending;
        ResortItems();
    }

    public void ApplyGroupBy(string? column)
    {
        GroupColumn = column;
        ResortItems();
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private async Task OpenSessionAndLoadAsync()
    {
        if (_site == null) return;

        ShowBusy(true);
        HideError();

        try
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            await Task.Run(() =>
            {
                _session?.Dispose();
                _session = null;
                var opts    = BuildSessionOptions(_site);
                var session = new Session();
                session.Open(opts);
                _session = session;
            }, ct);

            if (ct.IsCancellationRequested) return;

            await LoadDirectoryAsync(_currentPath, ct);
            UpButton.IsEnabled = _currentPath != "/";
            StatusText.Text    = $"Connected to {_site.Host}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowError($"Cannot connect to {_site?.Host}: {ex.Message}");
        }
        finally
        {
            ShowBusy(false);
        }
    }

    private static SessionOptions BuildSessionOptions(FtpSiteEntry site)
    {
        var password = FtpSiteDb.DecryptPassword(site.EncryptedPassword);
        var proto    = site.Protocol switch
        {
            FtpProtocol.Ftp  => WinSCP.Protocol.Ftp,
            FtpProtocol.Ftps => WinSCP.Protocol.Ftp,
            FtpProtocol.Sftp => WinSCP.Protocol.Sftp,
            FtpProtocol.Scp  => WinSCP.Protocol.Scp,
            _                => WinSCP.Protocol.Ftp,
        };

        var opts = new SessionOptions
        {
            Protocol   = proto,
            HostName   = site.Host,
            PortNumber = site.Port,
            UserName   = string.IsNullOrEmpty(site.Username) ? "anonymous" : site.Username,
            Password   = password,
        };

        if (site.Protocol == FtpProtocol.Ftps)
            opts.FtpSecure = FtpSecure.Explicit;

        if (site.AcceptAnyHostKey)
            opts.SshHostKeyPolicy = SshHostKeyPolicy.GiveUpSecurityAndAcceptAny;
        else if (!string.IsNullOrEmpty(site.HostFingerprint))
            opts.SshHostKeyFingerprint = site.HostFingerprint;

        return opts;
    }

    private async Task LoadDirectoryAsync(string path, CancellationToken ct)
    {
        if (_session == null) return;

        var items = await Task.Run(() =>
        {
            var dir    = _session.ListDirectory(path);
            var result = new List<RemoteFsItem>();
            foreach (RemoteFileInfo fi in dir.Files)
            {
                if (fi.Name is "." or "..") continue;
                result.Add(new RemoteFsItem
                {
                    Name        = fi.Name,
                    IsDirectory = fi.IsDirectory,
                    Size        = fi.Length,
                    Modified    = fi.LastWriteTime,
                });
            }
            return result;
        }, ct);

        if (ct.IsCancellationRequested) return;

        RemoteItems.Clear();
        foreach (var item in SortedItems(items)) RemoteItems.Add(item);
        _currentPath       = path;
        PathText.Text      = path;
        UpButton.IsEnabled = path != "/";
        StatusText.Text    = $"{RemoteItems.Count} item(s)  \u00B7  {path}";
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private IEnumerable<RemoteFsItem> SortedItems(IEnumerable<RemoteFsItem> source)
    {
        // When grouped by type, type becomes the primary sort key.
        bool groupByType = GroupColumn == "Type";

        return (SortColumn, SortAscending, groupByType) switch
        {
            (_, _, true) when SortAscending  => source.OrderBy(i => i.TypeText).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            (_, _, true)                     => source.OrderBy(i => i.TypeText).ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
            (FtpSortColumn.Size,     true,  _) => source.OrderBy(i => i.IsDirectory).ThenBy(i => i.Size),
            (FtpSortColumn.Size,     false, _) => source.OrderBy(i => i.IsDirectory).ThenByDescending(i => i.Size),
            (FtpSortColumn.Modified, true,  _) => source.OrderBy(i => i.IsDirectory).ThenBy(i => i.Modified),
            (FtpSortColumn.Modified, false, _) => source.OrderBy(i => i.IsDirectory).ThenByDescending(i => i.Modified),
            (FtpSortColumn.Type,     true,  _) => source.OrderBy(i => i.TypeText).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            (FtpSortColumn.Type,     false, _) => source.OrderByDescending(i => i.TypeText).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            (_,                      true,  _) => source.OrderBy(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _                                  => source.OrderBy(i => i.IsDirectory).ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    private void ResortItems()
    {
        var copy = RemoteItems.ToList();
        RemoteItems.Clear();
        foreach (var item in SortedItems(copy)) RemoteItems.Add(item);
    }

    // ── View layout ───────────────────────────────────────────────────────────

    private void UpdateViewLayout()
    {
        IconsPanel.Visibility   = ViewMode == FtpViewMode.Icons   ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility    = ViewMode == FtpViewMode.List    ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = ViewMode == FtpViewMode.Details ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── UI event handlers ─────────────────────────────────────────────────────

    private async void ListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        var item = (sender as ListView)?.SelectedItem as RemoteFsItem
                ?? (sender as GridView)?.SelectedItem as RemoteFsItem;
        if (item == null || !item.IsDirectory) return;

        var newPath = _currentPath.TrimEnd('/') + "/" + item.Name;
        ShowBusy(true);
        HideError();
        try
        {
            await LoadDirectoryAsync(newPath, _cts.Token);
        }
        catch (Exception ex)
        {
            ShowError($"Cannot open directory: {ex.Message}");
        }
        finally
        {
            ShowBusy(false);
        }
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath == "/") return;
        var parent = _currentPath.TrimEnd('/');
        var slash  = parent.LastIndexOf('/');
        var up     = slash <= 0 ? "/" : parent[..slash];

        ShowBusy(true);
        HideError();
        try { await LoadDirectoryAsync(up, _cts.Token); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { ShowBusy(false); }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) { await OpenSessionAndLoadAsync(); return; }
        ShowBusy(true);
        HideError();
        try { await LoadDirectoryAsync(_currentPath, _cts.Token); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { ShowBusy(false); }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectSilently();
        RemoteItems.Clear();
        PathText.Text      = "/";
        StatusText.Text    = "Disconnected";
        UpButton.IsEnabled = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenSessionAndLoadAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void DisconnectSilently()
    {
        _cts.Cancel();
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    private void ShowBusy(bool busy)
    {
        BusyRing.IsActive          = busy;
        IconsPanel.IsEnabled       = !busy;
        ListPanel.IsEnabled        = !busy;
        DetailsPanel.IsEnabled     = !busy;
        RefreshButton.IsEnabled    = !busy;
        DisconnectButton.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text          = message;
        ErrorPanel.Visibility   = Visibility.Visible;
        IconsPanel.Visibility   = Visibility.Collapsed;
        ListPanel.Visibility    = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Collapsed;
    }

    private void HideError()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        UpdateViewLayout();
    }
}
