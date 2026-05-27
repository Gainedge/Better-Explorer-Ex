using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace BetterExplorer.Controls;

public sealed partial class SettingsPage : UserControl
{
    // True once OnLoaded has finished restoring persisted selections.
    // Prevents InitializeComponent's default selection from overwriting saved settings.
    private bool _initialized;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public const string ThemeSettingKey = "App.Theme";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try {
            var ver = Windows.ApplicationModel.Package.Current.Id.Version;
            VersionText.Text = $"Version {ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
        } catch {
            VersionText.Text = "Version 1.0.0.0";
        }

        // Restore the persisted theme selection without triggering a save.
        var saved = ApplicationData.Current.LocalSettings.Values
            .TryGetValue(ThemeSettingKey, out var v) ? v as string : null;
        if (saved != null)
        {
            foreach (var item in ThemeRadioButtons.Items)
            {
                if (item is RadioButton rb && rb.Tag as string == saved)
                {
                    ThemeRadioButtons.SelectedItem = rb;
                    break;
                }
            }
        }

        _initialized = true;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (GeneralSection is null) return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        GeneralSection.Visibility    = tag == "General"    ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility      = tag == "About"      ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowHiddenFilesToggled(object sender, RoutedEventArgs e) { }

    private void OnShowExtensionsToggled(object sender, RoutedEventArgs e) { }

    /// <summary>Raised whenever the user picks a theme; subscribers should apply it to their window root.</summary>
    public static event Action<ElementTheme>? ThemeChangeRequested;

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;   // suppress premature save during InitializeComponent / restore
        if (ThemeRadioButtons.SelectedItem is not RadioButton rb) return;
        var tag = rb.Tag as string;
        var theme = tag switch {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default,
        };
        ApplicationData.Current.LocalSettings.Values[ThemeSettingKey] = tag ?? "Default";
        ThemeChangeRequested?.Invoke(theme);
    }
}
