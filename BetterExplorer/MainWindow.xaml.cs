using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace BetterExplorer;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("BENewIcon.ico");
    }
}
