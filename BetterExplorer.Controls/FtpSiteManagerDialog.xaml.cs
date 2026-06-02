using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterExplorer.Controls;

/// <summary>
/// Window that lists saved FTP/SFTP/SCP sites and allows add, edit, and delete.
/// Implemented as a Window (not ContentDialog) so it can host ContentDialogs inside it.
/// </summary>
public sealed partial class FtpSiteManagerDialog : Window
{
    public ObservableCollection<FtpSiteEntry> Sites { get; } = [];

    private readonly TaskCompletionSource _closedTcs = new();

    public Task WhenClosed => _closedTcs.Task;

    public FtpSiteManagerDialog()
    {
        InitializeComponent();
        Closed += (_, _) => _closedTcs.TrySetResult();
        _ = LoadSitesAsync();
    }

    private async Task LoadSitesAsync()
    {
        Sites.Clear();
        var list = await Task.Run(() => FtpSiteDb.Instance.LoadAll());
        foreach (var s in list) Sites.Add(s);
    }

    private void SiteListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = SiteListView.SelectedItem != null;
        EditButton.IsEnabled   = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var editDialog = new FtpSiteEditDialog(null) { XamlRoot = Content.XamlRoot };
        var result = (ContentDialogResult)await ShowDialogAsync(editDialog);
        if (result == ContentDialogResult.Primary && editDialog.ResultEntry is { } entry)
        {
            FtpSiteDb.Instance.Insert(entry);
            Sites.Add(entry);
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (SiteListView.SelectedItem is not FtpSiteEntry selected) return;
        var editDialog = new FtpSiteEditDialog(selected) { XamlRoot = Content.XamlRoot };
        var result = (ContentDialogResult)await ShowDialogAsync(editDialog);
        if (result == ContentDialogResult.Primary && editDialog.ResultEntry is { } updated)
        {
            FtpSiteDb.Instance.Update(updated);
            var idx = Sites.IndexOf(selected);
            if (idx >= 0) Sites[idx] = updated;
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SiteListView.SelectedItem is not FtpSiteEntry selected) return;

        var confirm = new ContentDialog
        {
            Title             = "Delete FTP Site",
            Content           = $"Delete \"{selected.DisplayName}\"?",
            PrimaryButtonText = "Delete",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = Content.XamlRoot,
        };
        var result = (ContentDialogResult)await ShowDialogAsync(confirm);
        if (result == ContentDialogResult.Primary)
        {
            FtpSiteDb.Instance.Delete(selected.Id);
            Sites.Remove(selected);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // Bridge IAsyncOperation<ContentDialogResult> -> Task without WinRT await ambiguity.
    private static Task<object> ShowDialogAsync(ContentDialog dlg)
    {
        var tcs = new TaskCompletionSource<object>();
        var op  = dlg.ShowAsync();
        op.Completed = (info, _) =>
        {
            try   { tcs.SetResult(info.GetResults()); }
            catch (Exception ex) { tcs.SetException(ex); }
        };
        return tcs.Task;
    }
}

