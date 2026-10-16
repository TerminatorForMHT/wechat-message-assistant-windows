using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WxToastNotifier.Data;
using WxToastNotifier.Models;
using WxToastNotifier.Security;
using WxToastNotifier.Services;

namespace WxToastNotifier;

public sealed partial class MainPage : Page
{
    private readonly StringBuilder _logSb = new();
    private MessageMonitor? _monitor;
    private ToastService? _toast;
    private AppConfig _config = new();

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = AppConfig.Load();

        DbStorageBox.Text = _config.DbStorage;
        PassphraseBox.Password = _config.Passphrase ?? "";
        KeysFileBox.Text = _config.KeysFile ?? "";
        IntervalBox.Text = _config.PollIntervalSeconds.ToString();
        MuteColumnBox.Text = _config.MuteColumn ?? "";

        Log("配置已加载自 " + Path.Combine(AppContext.BaseDirectory, "config.json"));

        if (string.IsNullOrWhiteSpace(DbStorageBox.Text))
        {
            var auto = AppConfig.TryAutoDetectDbStorage();
            if (!string.IsNullOrEmpty(auto))
            {
                DbStorageBox.Text = auto;
                Log("✓ 自动检测到数据库目录：" + auto);
            }
            else
            {
                Log("⚠️ 未检测到数据库目录，请在配置中手动填写 db_storage 路径");
            }
        }

        ShowCurrentAccount();
        UpdateWeChatStatus();

        if (string.IsNullOrWhiteSpace(KeysFileBox.Text) && string.IsNullOrWhiteSpace(PassphraseBox.Password))
        {
            Log("ℹ️ 未配置密钥：启动时将自动从微信进程内存提取（4.1+ WCDB，无需 passphrase/keysFile）");
        }
    }

    private void ShowCurrentAccount()
    {
        var wxid = Path.GetFileName(Path.GetDirectoryName(DbStorageBox.Text.Trim()));
        if (string.IsNullOrEmpty(wxid)) return;

        AccountText.Text = "当前账户：" + wxid;
        AccountText.Visibility = Visibility.Visible;
    }

    private async Task UpdateAccountDisplayAsync(WeChatDatabase db)
    {
        try
        {
            var selfName = await Task.Run(() => db.ReadSelfDisplayName());
            if (string.IsNullOrWhiteSpace(selfName)) return;

            AccountText.Text = "当前账户：" + selfName;
            AccountText.Visibility = Visibility.Visible;
            Log($"✓ 当前账户：{selfName}");
        }
        catch (Exception ex)
        {
            Log("读取当前账户信息失败：" + ex.Message);
        }
    }

    private void UpdateWeChatStatus()
    {
        var running = WeChatProcessService.IsRunning();
        WeChatStatusText.Text = running ? "✓ 微信已运行" : "⚠️ 微信未启动";
        OpenWeChatButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenWeChatButton_Click(object sender, RoutedEventArgs e)
    {
        Log("正在启动微信…");
        if (WeChatProcessService.Launch())
        {
            Log("✓ 已拉起微信，登录后点击「开始监听」即可");
        }
        else
        {
            Log("❌ 未找到微信安装路径，请手动启动微信");
        }
        UpdateWeChatStatus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _config = BuildConfigFromUi();
        _config.Save();
        Log("✓ 配置已保存");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            _config = BuildConfigFromUi();
            _config.Save();

            if (string.IsNullOrWhiteSpace(_config.DbStorage))
            {
                _config.DbStorage = AppConfig.TryAutoDetectDbStorage();
                DbStorageBox.Text = _config.DbStorage;
            }
            if (string.IsNullOrWhiteSpace(_config.DbStorage))
            {
                Log("❌ 未配置数据库目录");
                return;
            }

            // 密钥解析含内存扫描，放到后台线程避免阻塞 UI
            var keys = await Task.Run(() => DbKeyProvider.Resolve(_config.DbStorage, _config, Log));
            Log($"✓ 解析到 {keys.KeysByRelPath.Count} 个数据库密钥");
            if (keys.KeysByRelPath.Count == 0)
            {
                Log("❌ 未解析到任何数据库密钥（请确认微信已登录运行）");
                return;
            }

            var db = new WeChatDatabase(_config.DbStorage, keys, _config);

            await UpdateAccountDisplayAsync(db);

            _toast = new ToastService();
            _monitor = new MessageMonitor(db, _toast, Log, _config.PollIntervalSeconds);
            _monitor.NewMessage += _ => { /* 通知已在 Monitor 内推送并记录 */ };
            _monitor.Start();
        }
        catch (Exception ex)
        {
            Log("❌ 启动失败：" + ex.Message);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _monitor?.Stop();
    }

    private AppConfig BuildConfigFromUi() => new()
    {
        DbStorage = DbStorageBox.Text.Trim(),
        Passphrase = string.IsNullOrWhiteSpace(PassphraseBox.Password) ? null : PassphraseBox.Password.Trim(),
        KeysFile = string.IsNullOrWhiteSpace(KeysFileBox.Text) ? null : KeysFileBox.Text.Trim(),
        PollIntervalSeconds = int.TryParse(IntervalBox.Text.Trim(), out var iv) ? Math.Max(1, iv) : 2,
        MuteColumn = string.IsNullOrWhiteSpace(MuteColumnBox.Text) ? null : MuteColumnBox.Text.Trim(),
    };

    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "WxToastNotifier.log");

    private void Log(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{stamp}] {line}";
        try { File.AppendAllText(LogFilePath, entry + Environment.NewLine); } catch { }
        DispatcherQueue.TryEnqueue(() =>
        {
            _logSb.AppendLine(entry);
            LogText.Text = _logSb.ToString();
            LogText.UpdateLayout();
        });
    }
}