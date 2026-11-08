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
    private long _startEpoch;
    private Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);
    private Dictionary<string, bool> _muteFlags = new(StringComparer.Ordinal);
    private Dictionary<string, ContactEntry> _contacts = new(StringComparer.Ordinal);
    private readonly AvatarService _avatars;

    /// <summary>已推送过的「会话|时间戳」组合，防止同一条消息被重复推送。</summary>
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    /// <summary>聚合/占位会话：订阅号合集、服务号合集。它们与真实会话重复，且公众号文章推送也走这里，一律跳过。</summary>
    private static readonly HashSet<string> AggregatorUsernames = new(StringComparer.Ordinal)
    {
        "brandsessionholder",
        "brandservicesessionholder",
    };

    /// <summary>只推送监听启动之后到达的消息，容忍 120 秒时钟/写入偏差。</summary>
    private const long HistoryGraceSeconds = 120;

    /// <summary>新消息事件（在后台线程触发，UI 需自行调度）。</summary>
    public event Action<NewMessage>? NewMessage;

    public MessageMonitor(WeChatDatabase db, ToastService toast, Action<string> log, int intervalSeconds)
    {
        _db = db;
        _toast = toast;
        _log = log;
        _avatars = new AvatarService(log);
        _ = intervalSeconds; // 参数保留以兼容调用方；本实现采用 30ms 文件变化检测
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _startEpoch = DateTimeOffset.Now.ToUnixTimeSeconds();
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
            _avatars.Refresh(_contacts.Values); // 启动时本地刷新一次头像映射，缺失的后台下载
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
        var candidates = new List<SessionRecord>();
        var minTs = _startEpoch - HistoryGraceSeconds;

        foreach (var s in sessions)
        {
            if (string.IsNullOrEmpty(s.Username)) continue;
            if (AggregatorUsernames.Contains(s.Username)) continue;

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

            // 跳过历史消息：打开/同步微信时批量刷入的旧消息时间戳早于监听启动时间
            if (s.LastTimestamp < minTs) continue;

            // 同一条消息只推一次
            if (!_notified.Add($"{s.Username}|{s.LastTimestamp}")) continue;

            if (_muteFlags.TryGetValue(s.Username, out var m) && m)
            {
                _log($"[免打扰] 跳过 {ResolveTitle(s)}");
                continue;
            }

            candidates.Add(s);
        }

        if (_notified.Count > 2000) _notified.Clear();

        var selfFlags = _db.ReadSelfSenderLatest(candidates);
        foreach (var s in candidates)
        {
            if (selfFlags.TryGetValue(s.Username, out var self))
            {
                if (self) continue; // 自己发出的消息不推送
            }
            else if (s.UnreadCount <= 0)
            {
                continue; // 无法判定发送者且无未读：要么是自己发的，要么已读，跳过
            }

            var msg = new NewMessage(s.Username, ResolveTitle(s), s.Summary!, s.LastTimestamp);
            _toast.Show(msg, _avatars.TryGet(s.Username));
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