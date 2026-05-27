using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Storage;

namespace BetterExplorer.Controls;

public sealed partial class SettingsWindow : Window
{
    private const int WindowWidth  = 960;
    private const int WindowHeight = 600;

    public SettingsWindow()
    {
        InitializeComponent();

        // Size and center on the screen.
        AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
        CenterOnScreen();

        // Extend content into the title bar and use the custom drag strip.
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        // Caption button backgrounds are always transparent (Mica shows through).
        AppWindow.TitleBar.ButtonBackgroundColor         = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        SetTitleBar(TitleBarGrid);

        // Apply persisted theme immediately and sync caption button foreground.
        ApplyPersistedTheme();

        SettingsPage.ThemeChangeRequested += OnThemeChangeRequested;
        Closed += (_, _) => SettingsPage.ThemeChangeRequested -= OnThemeChangeRequested;
    }

    private void ApplyPersistedTheme()
    {
        var saved = ApplicationData.Current.LocalSettings.Values
            .TryGetValue(SettingsPage.ThemeSettingKey, out var v) ? v as string : null;
        var theme = saved switch {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default,
        };
        ApplyTheme(theme);
    }

    private void OnThemeChangeRequested(ElementTheme theme) => ApplyTheme(theme);

    private void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = theme;

        // Resolve actual dark/light from Default using the system setting.
        bool isDark = theme == ElementTheme.Dark ||
                     (theme == ElementTheme.Default &&
                      Application.Current.RequestedTheme == ApplicationTheme.Dark);

        var fg = isDark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor         = fg;
        AppWindow.TitleBar.ButtonHoverForegroundColor    = fg;
        AppWindow.TitleBar.ButtonPressedForegroundColor  = fg;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80)
            : Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A);
    }

    private void CenterOnScreen()
    {
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea    = displayArea.WorkArea;
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width  - WindowWidth)  / 2,
            workArea.Y + (workArea.Height - WindowHeight) / 2));
    }
}
