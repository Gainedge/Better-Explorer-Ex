using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace BetterExplorer.Controls.Settings;

public sealed partial class GeneralSettingsPage : Page
{
    private bool _initialized;

    public GeneralSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TeraCopyHelper.InvalidateCache();
        bool teraCopyAvailable = TeraCopyHelper.IsAvailable();
        FileOpTeraCopy.IsEnabled   = teraCopyAvailable;
        TeraCopyNotFoundBar.IsOpen = !teraCopyAvailable;

        EverythingSearch.InvalidateCache();
        bool everythingAvailable = EverythingSearch.IsAvailable();
        SearchEngineEverything.IsEnabled = everythingAvailable;
        EverythingNotFoundBar.IsOpen     = !everythingAvailable;

        // Restore persisted search engine selection without triggering a save.
        var savedEngine = ApplicationData.Current.LocalSettings.Values
            .TryGetValue(SettingsPage.SearchEngineKey, out var ev) ? ev as string : null;
        if (savedEngine == "Everything" && everythingAvailable)
            SearchEngineEverything.IsChecked = true;
        else
            SearchEngineWindowsSearch.IsChecked = true;

        // Restore persisted file-op handler selection without triggering a save.
        var savedHandler = ApplicationData.Current.LocalSettings.Values
            .TryGetValue(SettingsPage.FileOpHandlerKey, out var hv) ? hv as string : null;
        if (savedHandler == "TeraCopy" && teraCopyAvailable)
            FileOpTeraCopy.IsChecked = true;
        else
            FileOpSystem.IsChecked = true;

        _initialized = true;
    }

    private void OnShowHiddenFilesToggled(object sender, RoutedEventArgs e) { }

    private void OnShowExtensionsToggled(object sender, RoutedEventArgs e) { }

    private void OnSearchEngineChecked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag as string ?? "WindowsSearch";
        ApplicationData.Current.LocalSettings.Values[SettingsPage.SearchEngineKey] = tag;
        SettingsPage.RaiseSearchEngineChanged(tag);
    }

    private void OnFileOpHandlerChecked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag as string ?? "System";
        ApplicationData.Current.LocalSettings.Values[SettingsPage.FileOpHandlerKey] = tag;
        SettingsPage.RaiseFileOpHandlerChanged(tag);
    }
}
