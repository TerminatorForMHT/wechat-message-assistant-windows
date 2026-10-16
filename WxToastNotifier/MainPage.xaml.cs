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

        if (string.IsNullOrWhiteSpace(KeysFileBox.Text) && string.IsNullOrWhiteSpace(PassphraseBox.Password))
        {
            Log("⚠️ 尚未配置密钥：请提供 passphrase，或用 Python 端提取 all_keys.json 后填写 keysFile");
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _config = BuildConfigFromUi();
        _config.Save();
        Log("✓ 配置已保存");
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
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

            var keys = DbKeyProvider.Resolve(_config.DbStorage, _config);
            Log($"✓ 解析到 {keys.KeysByRelPath.Count} 个数据库密钥");

            var db = new WeChatDatabase(_config.DbStorage, keys, _config);
            _toast = new ToastService();
            _monitor = new MessageMonitor(db, _toast, Log, _config.PollIntervalSeconds);
            _monitor.NewMessage += _ => { /* 通知已在 Monitor 内推送并记录 */ };
            _monitor.Start();
        }
        catch (Exception ex)
        {
            Log("❌ 启动失败：" + ex.Message);
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

    private void Log(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        DispatcherQueue.TryEnqueue(() =>
        {
            _logSb.AppendLine($"[{stamp}] {line}");
            LogText.Text = _logSb.ToString();
            LogText.UpdateLayout();
        });
    }
}