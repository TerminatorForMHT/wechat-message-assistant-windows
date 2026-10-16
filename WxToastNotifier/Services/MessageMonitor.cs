using WxToastNotifier.Data;
using WxToastNotifier.Models;

namespace WxToastNotifier.Services;

/// <summary>
/// 轮询解密后的 session.db，检测新消息，按免打扰标记过滤后触发通知。
/// </summary>
public sealed class MessageMonitor
{
    private readonly WeChatDatabase _db;
    private readonly ToastService _toast;
    private readonly Action<string> _log;
    private readonly int _intervalSeconds;

    private CancellationTokenSource? _cts;
    private Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);
    private Dictionary<string, bool> _muteFlags = new(StringComparer.Ordinal);
    private Dictionary<string, ContactEntry> _contacts = new(StringComparer.Ordinal);

    /// <summary>新消息事件（在后台线程触发，UI 需自行调度）。</summary>
    public event Action<NewMessage>? NewMessage;

    public MessageMonitor(WeChatDatabase db, ToastService toast, Action<string> log, int intervalSeconds)
    {
        _db = db;
        _toast = toast;
        _log = log;
        _intervalSeconds = Math.Max(1, intervalSeconds);
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        try
        {
            _toast.Register();
            if (!_toast.IsRegistered)
                _log($"⚠️ Toast 注册失败：{_toast.LastError}");

            _db.IntrospectSchema();
            var muteCol = _db.MuteColumn;
            _log(muteCol is null
                ? "⚠️ 未识别到免打扰标记列（将推送所有会话），如需过滤请在 config.json 中设置 MuteColumn"
                : $"✓ 免打扰标记列：{muteCol}");
            _log($"会话表列：{string.Join(", ", _db.SessionColumns)}");

            _contacts = _db.ReadContacts();
            RefreshMuteFlags();
            InitializeBaseline();
        }
        catch (Exception ex)
        {
            _log($"❌ 初始化失败：{ex.Message}");
        }

        _ = Task.Run(() => RunLoop(_cts.Token), _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _log("已停止监听");
    }

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                _log($"轮询异常：{ex.Message}");
            }

            try { Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void InitializeBaseline()
    {
        var sessions = _db.ReadSessions();
        _lastSeen = sessions
            .Where(s => !string.IsNullOrEmpty(s.Username))
            .ToDictionary(s => s.Username, s => s.LastTimestamp, StringComparer.Ordinal);
        _log($"✓ 已建立基线（{_lastSeen.Count} 个会话），开始监听新消息…");
    }

    private void Poll()
    {
        var sessions = _db.ReadSessions();
        var newMessages = new List<NewMessage>();

        foreach (var s in sessions)
        {
            if (string.IsNullOrEmpty(s.Username)) continue;

            bool isNew;
            if (!_lastSeen.TryGetValue(s.Username, out var prevTs))
            {
                // 新增会话：仅当有未读时才提示
                isNew = s.UnreadCount > 0;
            }
            else
            {
                isNew = s.LastTimestamp > prevTs;
            }

            _lastSeen[s.Username] = Math.Max(prevTs, s.LastTimestamp);

            if (!isNew) continue;
            if (string.IsNullOrWhiteSpace(s.Summary)) continue;

            bool muted = _muteFlags.TryGetValue(s.Username, out var m) && m;
            if (muted) continue; // 免打扰会话不推送

            var title = ResolveTitle(s);
            newMessages.Add(new NewMessage(s.Username, title, s.Summary, s.LastTimestamp));
        }

        // 每隔一段时间刷新免打扰与联系人
        if (++_refreshCounter % 60 == 0)
        {
            RefreshMuteFlags();
        }

        foreach (var msg in newMessages)
        {
            _toast.Show(msg);
            NewMessage?.Invoke(msg);
            _log($"[通知] {msg.DisplayName}: {msg.Content}");
        }
    }

    private int _refreshCounter;

    private void RefreshMuteFlags() => _muteFlags = _db.ReadMuteFlags();

    private string ResolveTitle(SessionRecord s)
    {
        if (!string.IsNullOrWhiteSpace(s.LastSenderDisplayName))
            return s.LastSenderDisplayName!;

        if (_contacts.TryGetValue(s.Username, out var c))
            return c.DisplayName;

        return s.Username;
    }
}