using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace BetterExplorer.Controls;

public sealed partial class SettingsPage : UserControl
{
    // ── Setting keys (used by sub-pages) ────────────────────────────────────
    public const string ThemeSettingKey  = "App.Theme";
    public const string FileOpHandlerKey = "App.FileOpHandler";
    public const string SearchEngineKey  = "App.SearchEngine";
    // General (Settings page) view defaults
    public const string ShowHiddenFilesKey       = "App.ShowHiddenFiles";
    public const string ShowFileExtensionsKey    = "App.ShowFileExtensions";
    // Per-browser overrides — independent from the General defaults above
    public const string BrowserShowHiddenFilesKey    = "App.Browser.ShowHiddenFiles";
    public const string BrowserShowFileExtensionsKey = "App.Browser.ShowFileExtensions";

    // ── Events consumed by MainWindow ────────────────────────────────────────
    public static event Action<ElementTheme>? ThemeChangeRequested;
    public static event Action<string>?       FileOpHandlerChanged;
    public static event Action<string>?       SearchEngineChanged;

    // Internal helpers called by sub-pages
    internal static void RaiseThemeChangeRequested(ElementTheme theme)
        => ThemeChangeRequested?.Invoke(theme);
    internal static void RaiseFileOpHandlerChanged(string handler)
        => FileOpHandlerChanged?.Invoke(handler);
    internal static void RaiseSearchEngineChanged(string engine)
        => SearchEngineChanged?.Invoke(engine);

    /// <summary>Raised when the user clicks the footer Close button.</summary>
    public event EventHandler? CloseRequested;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Navigate to General on first load.
        ContentFrame.Navigate(typeof(Settings.GeneralSettingsPage));
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var pageType = tag switch {
            "General"    => typeof(Settings.GeneralSettingsPage),
            "Appearance" => typeof(Settings.AppearanceSettingsPage),
            "About"      => typeof(Settings.AboutSettingsPage),
            _            => typeof(Settings.GeneralSettingsPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType);
    }

    private void OnFooterCloseClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ── Legacy helpers kept for callers that use the static properties ───────

    /// <summary>Returns the currently persisted file-operation handler.</summary>
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

    /// <summary>Returns the currently persisted search engine.</summary>
    public static string SearchEngine
    {
        get
        {
            try {
                return ApplicationData.Current.LocalSettings.Values
                    .TryGetValue(SearchEngineKey, out var v) ? v as string ?? "WindowsSearch" : "WindowsSearch";
            } catch { return "WindowsSearch"; }
        }
    }

    /// <summary>Returns the persisted show-hidden-files setting (default false).</summary>
    public static bool ShowHiddenFiles
    {
        get
        {
            try {
                return ApplicationData.Current.LocalSettings.Values
                    .TryGetValue(ShowHiddenFilesKey, out var v) && v is bool b ? b : false;
            } catch { return false; }
        }
    }

    /// <summary>Returns the persisted show-file-extensions setting (default true).</summary>
    public static bool ShowFileExtensions
    {
        get
        {
            try {
                return ApplicationData.Current.LocalSettings.Values
                    .TryGetValue(ShowFileExtensionsKey, out var v) ? (v is bool b ? b : true) : true;
            } catch { return true; }
        }
    }
}
