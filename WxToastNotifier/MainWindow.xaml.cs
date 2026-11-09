using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using WxToastNotifier.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WxToastNotifier;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private TrayIcon? _trayIcon;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        SetupTrayIcon();
        SetupCloseToTray();
    }

    private void SetupTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _trayIcon = new TrayIcon(hwnd, "微信消息助手", iconPath, ShowFromTray, ExitApplication);
    }

    private void SetupCloseToTray()
    {
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            HideToTray();
        };
    }

    private void HideToTray() => AppWindow.Hide();

    /// <summary>供单实例重定向调用：从托盘恢复并前置主窗口。</summary>
    public void Restore() => ShowFromTray();

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
        Application.Current.Exit();
    }
}