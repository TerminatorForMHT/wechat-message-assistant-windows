using WxToastNotifier.Data;
using WxToastNotifier.Models;

namespace WxToastNotifier.Services;

/// <summary>
/// 30ms 轻量轮询检测微信 db 文件变化，检测到变化才解密读新消息，
/// 按免打扰标记过滤后触发通知（与 Python 版 poll_interval=0.03 一致）。
/// </summary>
public sealed class MessageMonitor
{
    private readonly WeChatDatabase _db;
    private readonly ToastService _toast;
    private readonly Action<string> _log;

    private CancellationTokenSource? _cts;
    private long _lastSessionSig;
    private long _lastContactSig;
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
        _ = intervalSeconds; // 参数保留以兼容调用方；本实现采用 30ms 文件变化检测
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
            _log($"✓ 免打扰标记列：{muteCol}（contact 表，0=正常，1=免打扰）");
            _log($"会话表列：{string.Join(", ", _db.SessionColumns)}");

            _contacts = _db.ReadContacts();
            RefreshMuteFlags();
            InitializeBaseline();
            SyncSignature();
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
        var pollInterval = TimeSpan.FromMilliseconds(30);
        while (!token.IsCancellationRequested)
        {
            if (AnyDbChanged())
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    _log($"轮询异常：{ex.Message}");
                }
            }

            try { Task.Delay(pollInterval, token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>轻量检查 session/contact 的 db+wal 是否有变化（绝不解密）。</summary>
    private bool AnyDbChanged()
    {
        var (sessionSig, contactSig) = CurrentSig();
        var changed = sessionSig != _lastSessionSig || contactSig != _lastContactSig;
        _lastSessionSig = sessionSig;
        _lastContactSig = contactSig;
        return changed;
    }

    private void SyncSignature()
    {
        (_lastSessionSig, _lastContactSig) = CurrentSig();
    }

    private (long Session, long Contact) CurrentSig()
    {
        var root = _db.DbStorage;
        var sessionSig = FileSig(root, "session", "session.db") + FileSig(root, "session", "session.db-wal");
        var contactSig = FileSig(root, "contact", "contact.db") + FileSig(root, "contact", "contact.db-wal");
        return (sessionSig, contactSig);
    }

    private static long FileSig(string root, string dir, string name)
    {
        try
        {
            var fi = new FileInfo(Path.Combine(root, dir, name));
            return fi.Exists ? fi.Length + fi.LastWriteTimeUtc.Ticks : 0;
        }
        catch { return 0; }
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
        RefreshMuteFlags(); // 每轮先刷新免打扰标记，切换免打扰后能及时生效

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

            if (_muteFlags.TryGetValue(s.Username, out var m) && m)
            {
                _log($"[免打扰] 跳过 {ResolveTitle(s)}");
                continue;
            }

            var title = ResolveTitle(s);
            newMessages.Add(new NewMessage(s.Username, title, s.Summary, s.LastTimestamp));
        }

        var selfFlags = _db.ReadSelfSenderLatest(newMessages.Select(m => m.Username).ToList());
        foreach (var msg in newMessages)
        {
            if (selfFlags.TryGetValue(msg.Username, out var self) && self)
                continue; // 自己发出的消息不推送

            _toast.Show(msg);
            NewMessage?.Invoke(msg);
            _log($"[通知] {msg.DisplayName}: {msg.Content}");
        }
    }

    private int _lastMutedCount = -1;

    private void RefreshMuteFlags()
    {
        _muteFlags = _db.ReadMuteFlags();
        var muted = _muteFlags.Count(m => m.Value);
        if (muted != _lastMutedCount)
        {
            _lastMutedCount = muted;
            _log($"✓ 免打扰会话已加载：{muted} 个");
        }
    }

    private string ResolveTitle(SessionRecord s)
    {
        if (!string.IsNullOrWhiteSpace(s.LastSenderDisplayName))
            return s.LastSenderDisplayName!;

        if (_contacts.TryGetValue(s.Username, out var c))
            return c.DisplayName;

        return s.Username;
    }
}