using System.Diagnostics;
using Microsoft.Win32;

namespace WxToastNotifier.Services;

/// <summary>
/// 检测微信（Weixin.exe）是否在运行，并负责拉起已安装的微信。
/// </summary>
public static class WeChatProcessService
{
    private const string ProcessName = "Weixin";
    private const string DefaultInstallDir = @"C:\Program Files\Tencent\Weixin";

    public static bool IsRunning()
    {
        return Process.GetProcessesByName(ProcessName).Length > 0;
    }

    /// <summary>定位微信安装目录（优先注册表，回退默认路径）。</summary>
    public static string? FindInstallDir()
    {
        var candidates = new[]
        {
            ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Tencent\Weixin"),
            ReadRegistry(Registry.CurrentUser, @"Software\Tencent\Weixin"),
            DefaultInstallDir
        };

        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (File.Exists(Path.Combine(dir, "Weixin.exe")))
                return dir;
        }
        return null;
    }

    /// <summary>启动微信；成功返回 true。</summary>
    public static bool Launch()
    {
        var dir = FindInstallDir();
        if (dir == null) return false;

        var exe = Path.Combine(dir, "Weixin.exe");
        if (!File.Exists(exe)) return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true
        });
        return true;
    }

    private static string? ReadRegistry(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("InstallPath") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}