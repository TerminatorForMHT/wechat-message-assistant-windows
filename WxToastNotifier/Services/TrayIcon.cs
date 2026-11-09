using System.Runtime.InteropServices;

namespace WxToastNotifier.Services;

/// <summary>
/// 原生 Win32 托盘图标 + TrackPopupMenu 菜单。
/// 不使用 H.NotifyIcon 的 XAML 菜单转换：SecondWindow 模式有文字截断/图标乱码问题，
/// PopupMenu 模式下 MenuFlyoutItem 的 Click 回调不被触发（菜单点了没反应）。
/// 这里用 TPM_RETURNCMD 同步拿到菜单 id 再分发，行为完全可控。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const uint WM_TRAYICON = WM_APP + 1;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const uint MF_STRING = 0x00;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_LEFTALIGN = 0x00;
    private const uint TPM_TOPALIGN = 0x00;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x02;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private const uint CMD_SHOW = 1;
    private const uint CMD_EXIT = 2;

    private readonly IntPtr _hwnd;
    private readonly Action _onShow;
    private readonly Action _onExit;
    private readonly string _iconPath;
    private readonly string _toolTip;

    private readonly SubclassProc _subclassProc;   // 必须持有引用，否则被 GC 回收后回调失效
    private readonly uint _taskbarCreatedMessage;
    private bool _added;
    private bool _disposed;

    public TrayIcon(IntPtr hwnd, string toolTip, string iconPath, Action onShow, Action onExit)
    {
        _hwnd = hwnd;
        _toolTip = toolTip;
        _iconPath = iconPath;
        _onShow = onShow;
        _onExit = onExit;
        _subclassProc = WndProc;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1, IntPtr.Zero);
        ApplyTheme(IsSystemDarkTheme());
        AddIcon();
    }

    /// <summary>
    /// 让原生菜单跟随系统深浅色。uxtheme 的这几个导出没有公开名字，只能按序号调用，
    /// 但自 Win10 1903 起一直稳定。
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight
        SetPreferredAppMode(dark ? 2 : 3);
        FlushMenuThemes();

        AllowDarkModeForWindow(_hwnd, dark);
        SetWindowTheme(_hwnd, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private void AddIcon()
    {
        var icon = LoadImageW(IntPtr.Zero, _iconPath, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);

        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = icon,
            szTip = _toolTip,
        };

        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == WM_TRAYICON)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    _onShow();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    ShowContextMenu();
                    return IntPtr.Zero;
            }
        }
        else if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // 资源管理器重启后托盘图标会被清空，需要重新注册
            _added = false;
            AddIcon();
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        // 用户可能中途切换了系统主题，每次弹菜单前重新对齐
        ApplyTheme(IsSystemDarkTheme());

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(menu, MF_STRING, CMD_SHOW, "显示主窗口");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CMD_EXIT, "退出");

            GetCursorPos(out var pt);
            // 必须先把窗口设为前台，否则菜单在点击别处后不会自动消失
            SetForegroundWindow(_hwnd);

            var cmd = TrackPopupMenuEx(menu, TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CMD_SHOW:
                    _onShow();
                    break;
                case CMD_EXIT:
                    _onExit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // uxtheme 未公开导出，按序号绑定：135=SetPreferredAppMode, 136=FlushMenuThemes, 133=AllowDarkModeForWindow
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowDarkModeForWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
}
