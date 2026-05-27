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
        // Always rescan for TeraCopy on every page construction so a freshly
        // installed copy is detected immediately without restarting the app.
        TeraCopyHelper.InvalidateCache();
        Loaded += OnLoaded;
    }

    // ── Setting keys ─────────────────────────────────────────────────────────

    public const string ThemeSettingKey  = "App.Theme";
    public const string FileOpHandlerKey = "App.FileOpHandler";

    /// <summary>Returns the currently persisted file-operation handler: "System" or "TeraCopy".</summary>
    public static string FileOpHandler
    {
        get
        {
            try {
                return ApplicationData.Current.LocalSettings.Values
                    .TryGetValue(FileOpHandlerKey, out var v) ? v as string ?? "System" : "System";
            } catch { return "System"; }
        }
    }

    /// <summary>Raised when the user changes the file-operation handler setting.</summary>
    public static event Action<string>? FileOpHandlerChanged;

    // ── Loaded ────────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try {
            var ver = Windows.ApplicationModel.Package.Current.Id.Version;
            VersionText.Text = $"Version {ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
        } catch {
            VersionText.Text = "Version 1.0.0.0";
        }

        // Re-scan for TeraCopy each time the page is shown so the option becomes
        // enabled if TeraCopy was installed since the app started.
        TeraCopyHelper.InvalidateCache();
        bool teraCopyAvailable = TeraCopyHelper.IsAvailable();
        FileOpTeraCopy.IsEnabled             = teraCopyAvailable;
        TeraCopyNotFoundText.Visibility      = teraCopyAvailable ? Visibility.Collapsed : Visibility.Visible;

        // Restore persisted theme selection without triggering a save.
        var savedTheme = ApplicationData.Current.LocalSettings.Values
            .TryGetValue(ThemeSettingKey, out var tv) ? tv as string : null;
        if (savedTheme != null)
        {
            foreach (var item in ThemeRadioButtons.Items)
            {
                if (item is RadioButton rb && rb.Tag as string == savedTheme)
                {
                    ThemeRadioButtons.SelectedItem = rb;
                    break;
                }
            }
        }

        // Restore persisted file-op handler selection without triggering a save.
        var savedHandler = ApplicationData.Current.LocalSettings.Values
            .TryGetValue(FileOpHandlerKey, out var hv) ? hv as string : null;
        if (savedHandler != null)
        {
            foreach (var item in FileOpHandlerRadioButtons.Items)
            {
                if (item is RadioButton rb && rb.Tag as string == savedHandler && rb.IsEnabled)
                {
                    FileOpHandlerRadioButtons.SelectedItem = rb;
                    break;
                }
            }
        }

        _initialized = true;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (GeneralSection is null) return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        GeneralSection.Visibility    = tag == "General"    ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility      = tag == "About"      ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── General toggles ───────────────────────────────────────────────────────

    private void OnShowHiddenFilesToggled(object sender, RoutedEventArgs e) { }

    private void OnShowExtensionsToggled(object sender, RoutedEventArgs e) { }

    // ── File-operation handler ────────────────────────────────────────────────

    private void OnFileOpHandlerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not RadioButtons rb || rb.SelectedItem is not RadioButton selected) return;
        var tag = selected.Tag as string ?? "System";
        ApplicationData.Current.LocalSettings.Values[FileOpHandlerKey] = tag;
        FileOpHandlerChanged?.Invoke(tag);
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

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