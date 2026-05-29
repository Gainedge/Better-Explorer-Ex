using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BetterExplorer.Controls;

// ── Data model ─────────────────────────────────────────────────────────────

/// <summary>One icon slot in the picker grid.</summary>
public sealed class IconEntry : INotifyPropertyChanged
{
    private WriteableBitmap? _image;

    public int    Index      { get; init; }
    public string FilePath   { get; init; } = string.Empty;
    public string IndexLabel => $"Icon #{Index}";

    public WriteableBitmap? Image
    {
        get => _image;
        set { _image = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Dialog ─────────────────────────────────────────────────────────────────

/// <summary>
/// Icon-picker dialog.  After <c>await ShowAsync()</c> returns Primary:
///   <see cref="SelectedFile"/>  — absolute path to the resource file
///   <see cref="SelectedIndex"/> — 0-based icon index inside that file
/// </summary>
public sealed partial class FolderIconPickerDialog : ContentDialog
{
    // ── Public outputs ──────────────────────────────────────────────────────

    public string? SelectedFile  { get; private set; }
    public int     SelectedIndex { get; private set; }

    // ── Internals ───────────────────────────────────────────────────────────

    private const int IconSize        = 32;
    private const int BatchSize       = 20;   // icons rendered per UI tick

    private readonly ObservableCollection<IconEntry> _icons = [];
    private CancellationTokenSource _loadCts = new();

    // ── Constructor ─────────────────────────────────────────────────────────

    public FolderIconPickerDialog()
    {
        InitializeComponent();
        IconGrid.ItemsSource = _icons;
    }

    // ── Loaded trigger ──────────────────────────────────────────────────────

    /// <summary>
    /// Call this after the dialog is attached to a XamlRoot to start loading
    /// the default imageres.dll icons.
    /// </summary>
    public void StartWithDefaultFile()
    {
        string imageres = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "imageres.dll");
        SourceFileBox.Text = imageres;
        _ = LoadIconsAsync(imageres);
    }

    // ── Source-file controls ────────────────────────────────────────────────

    private void SourceFileBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = LoadIconsAsync(SourceFileBox.Text.Trim());
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetOwnerHwnd());
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".dll");
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".ico");
        picker.FileTypeFilter.Add(".icl");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        SourceFileBox.Text = file.Path;
        _ = LoadIconsAsync(file.Path);
    }

    // ── Icon loading ────────────────────────────────────────────────────────

    private async Task LoadIconsAsync(string filePath)
    {
        // Cancel any prior load
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _icons.Clear();
        IsPrimaryButtonEnabled = false;
        StatusText.Text = "Loading…";

        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            StatusText.Text = "File not found.";
            return;
        }

        // Count available icons (blocking, but fast enough for a DLL)
        int total = await Task.Run(() => NativeShell.ExtractIconCount(filePath), ct)
                              .ConfigureAwait(true);

        if (ct.IsCancellationRequested) return;

        if (total <= 0)
        {
            StatusText.Text = "No icons found in this file.";
            return;
        }

        StatusText.Text = $"{total} icon{(total == 1 ? "" : "s")}";

        // Pre-populate the grid with placeholder entries (image = null → shows nothing)
        for (int i = 0; i < total; i++)
            _icons.Add(new IconEntry { Index = i, FilePath = filePath });

        // Render icons in background batches
        for (int i = 0; i < total; i += BatchSize)
        {
            if (ct.IsCancellationRequested) return;

            int batchEnd = Math.Min(i + BatchSize, total);

            // Extract pixels off the UI thread
            var batch = await Task.Run(() =>
            {
                var results = new (byte[]? px, int w, int h)[batchEnd - i];
                for (int j = i; j < batchEnd; j++)
                {
                    if (ct.IsCancellationRequested) break;
                    var hbm = NativeShell.ExtractIconHBitmap(filePath, j, IconSize);
                    if (hbm != IntPtr.Zero)
                    {
                        try   { results[j - i] = NativeShell.HBitmapToPixels(hbm); }
                        finally { NativeShell.DeleteObject(hbm); }
                    }
                }
                return results;
            }, ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested) return;

            // Convert pixels → WriteableBitmap on the UI thread
            for (int j = 0; j < batch.Length; j++)
            {
                var (px, w, h) = batch[j];
                if (px is not null)
                    _icons[i + j].Image = NativeShell.PixelsToBitmapSync(px, w, h);
            }
        }
    }

    // ── Selection ───────────────────────────────────────────────────────────

    private void IconGrid_ItemClick(object sender, ItemClickEventArgs e) { }

    private void IconGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconGrid.SelectedItem is IconEntry entry)
        {
            SelectedFile  = entry.FilePath;
            SelectedIndex = entry.Index;
            IsPrimaryButtonEnabled = true;
        }
        else
        {
            IsPrimaryButtonEnabled = false;
        }
    }

    // ── Dialog lifecycle ────────────────────────────────────────────────────

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _loadCts.Cancel();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private IntPtr GetOwnerHwnd()
    {
        try
        {
            var window = Microsoft.UI.Xaml.Window.Current;
            if (window is not null)
                return WinRT.Interop.WindowNative.GetWindowHandle(window);
        }
        catch { }
        return IntPtr.Zero;
    }
}
