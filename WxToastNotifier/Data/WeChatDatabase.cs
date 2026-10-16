using Microsoft.Data.Sqlite;
using WxToastNotifier.Models;
using WxToastNotifier.Security;

namespace WxToastNotifier.Data;

/// <summary>
/// 解密微信数据库并读取会话/联系人，同时探测免打扰标记列。
/// 解密输出写入临时目录，绝不改动微信原始文件。
/// </summary>
public sealed class WeChatDatabase
{
    public const string SessionDb = @"session\session.db";
    public const string ContactDb = @"contact\contact.db";

    /// <summary>contact 表中已确认的消息免打扰标记列（0=正常，1=免打扰）。</summary>
    public const string MuteColumnName = "chat_room_notify";

    private readonly string _dbStorage;
    private readonly KeySet _keys;
    private readonly AppConfig _config;
    private readonly string _tempRoot;

    public WeChatDatabase(string dbStorage, KeySet keys, AppConfig config)
    {
        _dbStorage = dbStorage;
        _keys = keys;
        _config = config;
        _tempRoot = Path.Combine(Path.GetTempPath(), "WxToastNotifier");
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>会话表列名（introspect 后可用）。</summary>
    public IReadOnlyList<string> SessionColumns { get; private set; } = Array.Empty<string>();

    /// <summary>联系人表列名。</summary>
    public IReadOnlyList<string> ContactColumns { get; private set; } = Array.Empty<string>();

    /// <summary>免打扰标记列（默认 contact.chat_room_notify，可用 config.MuteColumn 覆盖）。</summary>
    public string MuteColumn { get; private set; } = MuteColumnName;

    /// <summary>会话表建表 SQL（用于人工核对免打扰字段）。</summary>
    public string? SessionTableSql { get; private set; }

    /// <summary>把某个加密 db 解密到临时文件（含 WAL 补齐），返回临时路径。</summary>
    public string DecryptToTemp(string relDb)
    {
        var key = _keys.For(relDb)
            ?? throw new InvalidOperationException($"缺少数据库密钥：{relDb}（请先提取密钥或配置 passphrase）");

        var src = Path.Combine(_dbStorage, relDb);
        if (!File.Exists(src))
            throw new FileNotFoundException($"微信数据库不存在：{src}");

        var safeName = relDb.Replace('\\', '_').Replace('/', '_');
        var dest = Path.Combine(_tempRoot, safeName);

        WeChatCrypto.FullDecrypt(src, dest, key);

        var wal = src + "-wal";
        if (File.Exists(wal))
            WeChatCrypto.DecryptWal(wal, dest, key);

        return dest;
    }

    /// <summary>列出会话/联系人表结构并探测免打扰列。</summary>
    public void IntrospectSchema()
    {
        var sessionPath = DecryptToTemp(SessionDb);
        SessionColumns = ReadTableColumns(sessionPath, "SessionTable", out var sessionSql);
        SessionTableSql = sessionSql;

        try
        {
            var contactPath = DecryptToTemp(ContactDb);
            ContactColumns = ReadTableColumns(contactPath, "contact", out _);
        }
        catch (Exception)
        {
            ContactColumns = Array.Empty<string>();
        }

        MuteColumn = DetectMuteColumn();
    }

    /// <summary>读取所有有消息的会话（含免打扰标记）。</summary>
    public List<SessionRecord> ReadSessions()
    {
        var path = DecryptToTemp(SessionDb);
        var result = new List<SessionRecord>();

        using var conn = OpenReadOnly(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT username, last_timestamp, last_msg_type, unread_count, summary, " +
            "last_msg_sender, last_sender_display_name FROM SessionTable WHERE last_timestamp > 0";

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new SessionRecord(
                GetString(r, "username") ?? "",
                GetInt64(r, "last_timestamp"),
                GetInt32(r, "last_msg_type"),
                GetInt32(r, "unread_count"),
                GetString(r, "summary"),
                GetString(r, "last_msg_sender"),
                GetString(r, "last_sender_display_name"),
                false));
        }
        return result;
    }

    /// <summary>读取会话免打扰标记（username → 是否免打扰），来自 contact 表。</summary>
    public Dictionary<string, bool> ReadMuteFlags()
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var muteCol = MuteColumn;
        try
        {
            var path = DecryptToTemp(ContactDb);
            using var conn = OpenReadOnly(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT username, \"{muteCol}\" FROM contact WHERE \"{muteCol}\" IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = GetString(r, "username");
                if (string.IsNullOrEmpty(name)) continue;
                result[name] = IntToBool(GetInt32(r, muteCol));
            }
        }
        catch (Exception)
        {
            // contact.db 或免打扰列不可用时，退回推送所有会话
        }
        return result;
    }

    /// <summary>读取联系人（username → 显示名）。</summary>
    public Dictionary<string, ContactEntry> ReadContacts()
    {
        var result = new Dictionary<string, ContactEntry>(StringComparer.Ordinal);
        try
        {
            var path = DecryptToTemp(ContactDb);
            using var conn = OpenReadOnly(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT username, nick_name, remark FROM contact WHERE username IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = GetString(r, "username");
                if (string.IsNullOrEmpty(name)) continue;
                result[name] = new ContactEntry(name, GetString(r, "nick_name"), GetString(r, "remark"));
            }
        }
        catch (Exception)
        {
            // contact.db 不可用时仅影响显示名，不影响通知内容
        }
        return result;
    }

    private string DetectMuteColumn()
    {
        if (!string.IsNullOrWhiteSpace(_config.MuteColumn))
            return _config.MuteColumn;
        return MuteColumnName;
    }

    private static bool IntToBool(int v) => v != 0;

    private static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    private static List<string> ReadTableColumns(string dbPath, string table, out string? createSql)
    {
        var columns = new List<string>();
        createSql = null;
        using var conn = OpenReadOnly(dbPath);
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT sql FROM sqlite_master WHERE type='table' AND name='{table}'";
            createSql = c.ExecuteScalar() as string;
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var name = GetString(r, "name");
                if (!string.IsNullOrEmpty(name)) columns.Add(name);
            }
        }
        return columns;
    }

    private static string? GetString(SqliteDataReader r, string col)
    {
        try
        {
            var idx = r.GetOrdinal(col);
            return r.IsDBNull(idx) ? null : r.GetString(idx);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    private static int GetInt32(SqliteDataReader r, string col)
    {
        try { var idx = r.GetOrdinal(col); return r.IsDBNull(idx) ? 0 : r.GetInt32(idx); }
        catch (IndexOutOfRangeException) { return 0; }
    }

    private static long GetInt64(SqliteDataReader r, string col)
    {
        try { var idx = r.GetOrdinal(col); return r.IsDBNull(idx) ? 0 : r.GetInt64(idx); }
        catch (IndexOutOfRangeException) { return 0; }
    }
}