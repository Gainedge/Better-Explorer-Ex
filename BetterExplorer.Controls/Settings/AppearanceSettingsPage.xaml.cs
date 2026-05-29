using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace BetterExplorer.Controls.Settings;

public sealed partial class AppearanceSettingsPage : Page
{
    private bool _initialized;

    public AppearanceSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restore persisted theme selection without triggering a save.
        var savedTheme = ApplicationData.Current.LocalSettings.Values
            .TryGetValue(SettingsPage.ThemeSettingKey, out var tv) ? tv as string : null;
        if      (savedTheme == "Light") ThemeLight.IsChecked   = true;
        else if (savedTheme == "Dark")  ThemeDark.IsChecked    = true;
        else                            ThemeDefault.IsChecked = true;

        _initialized = true;
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag as string;
        var theme = tag switch {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default,
        };
        ApplicationData.Current.LocalSettings.Values[SettingsPage.ThemeSettingKey] = tag ?? "Default";
        SettingsPage.RaiseThemeChangeRequested(theme);
    }
}
