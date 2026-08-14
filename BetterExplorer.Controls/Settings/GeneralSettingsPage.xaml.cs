using System;
using BetterExplorer.ShellApi.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

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

        // Restore persisted view-option settings without triggering a save.
        ShowHiddenFilesToggle.IsOn  = SettingsPage.ShowHiddenFiles;
        ShowExtensionsToggle.IsOn   = SettingsPage.ShowFileExtensions;

        // Restore persisted startup location without triggering a save.
        StartupLocationBox.Text = SettingsPage.StartupLocation ?? string.Empty;

        // Restore persisted restore-tabs toggle without triggering a save.
        RestoreTabsToggle.IsOn = SettingsPage.RestoreTabs;

        _initialized = true;
    }

    private void OnShowHiddenFilesToggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ApplicationData.Current.LocalSettings.Values[SettingsPage.ShowHiddenFilesKey] = ShowHiddenFilesToggle.IsOn;
    }

    private void OnRestoreTabsToggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ApplicationData.Current.LocalSettings.Values[SettingsPage.RestoreTabsKey] = RestoreTabsToggle.IsOn;
        SettingsPage.RaiseRestoreTabsChanged(RestoreTabsToggle.IsOn);
    }

    private void OnShowExtensionsToggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ApplicationData.Current.LocalSettings.Values[SettingsPage.ShowFileExtensionsKey] = ShowExtensionsToggle.IsOn;
    }

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

    private void OnStartupLocationTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        var text = StartupLocationBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
            ApplicationData.Current.LocalSettings.Values.Remove(SettingsPage.StartupLocationKey);
        else
            ApplicationData.Current.LocalSettings.Values[SettingsPage.StartupLocationKey] = text;
        SettingsPage.RaiseStartupLocationChanged(string.IsNullOrEmpty(text) ? null : text);
    }

    private async void OnBrowseStartupLocationClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        // Associate the picker with the app window.
        var hwnd = SettingsPage.GetMainWindowHandle?.Invoke() ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            StartupLocationBox.Text = folder.Path;
    }

    private void OnThisPcStartupLocationClick(object sender, RoutedEventArgs e)
    {
        // Windows.Storage.Pickers.FolderPicker can only return real filesystem paths,
        // so virtual folders like This PC can never come back from Browse…. Offer this
        // as a direct shortcut instead, using the same "::{GUID}" convention ShellListView
        // uses for known folders.
        StartupLocationBox.Text = $"::{NativeShell.FOLDERID_ComputerFolder:B}";
    }
}
