using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterExplorer.Controls.Settings;

public sealed partial class AboutSettingsPage : Page
{
    public AboutSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try {
            var ver = Windows.ApplicationModel.Package.Current.Id.Version;
            VersionText.Text = $"Version {ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
        } catch {
            VersionText.Text = "Version 1.0.0.0";
        }
    }
}
