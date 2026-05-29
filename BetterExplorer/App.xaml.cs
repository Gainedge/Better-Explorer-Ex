using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BetterExplorer;
/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application {
  private Window? _window;

  /// <summary>
  /// Initializes the singleton application object.  This is the first line of authored code
  /// executed, and as such is the logical equivalent of main() or WinMain().
  /// </summary>
  public App() {
    // Read the persisted theme BEFORE InitializeComponent so WinUI initialises
    // the theme system with the correct value from the very first frame,
    // preventing the initial black-background flash.
    var saved = Windows.Storage.ApplicationData.Current.LocalSettings.Values
        .TryGetValue(BetterExplorer.Controls.SettingsPage.ThemeSettingKey, out var v) ? v as string : null;
    if (saved == "Light") RequestedTheme = ApplicationTheme.Light;
    else if (saved == "Dark") RequestedTheme = ApplicationTheme.Dark;

    InitializeComponent();
  }

  /// <summary>
  /// Invoked when the application is launched.
  /// </summary>
  /// <param name="args">Details about the launch request and process.</param>
  protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args) {
    _window = new MainWindow();
    _window.Activate();
  }
}
