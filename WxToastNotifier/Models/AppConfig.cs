using System.Text.Json;
using System.Text.Json.Serialization;

namespace WxToastNotifier.Models;

/// <summary>应用配置，保存在可执行文件旁的 config.json。</summary>
public sealed class AppConfig
{
    /// <summary>微信 db_storage 目录（形如 …\xwechat_files\&lt;wxid&gt;\db_storage）。</summary>
    public string DbStorage { get; set; } = "";

    /// <summary>微信 4.1+ passphrase，64 位 hex 或 32 字节原文；与 KeysFile 二选一。</summary>
    public string? Passphrase { get; set; }

    /// <summary>既有 all_keys.json 路径（由 Python 提取工具生成）。</summary>
    public string? KeysFile { get; set; }

    /// <summary>会话轮询间隔（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>免打扰标记列名（若自动探测失败可手动覆盖）。</summary>
    public string? MuteColumn { get; set; }

    public static AppConfig Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        return new AppConfig();
    }

    public void Save(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    /// <summary>
    /// 自动定位微信 db_storage。多账户（多个 wxid_* 目录）时，选 db 最近写入时间
    /// 最新的那个（当前登录账户的库会持续写入，历史账户的库时间戳停留在早期）。
    /// </summary>
    public static string TryAutoDetectDbStorage()
    {
        var docRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "xwechat_files");
        if (!Directory.Exists(docRoot)) return "";

        string? best = null;
        var bestTime = DateTime.MinValue;

        foreach (var wxidDir in Directory.EnumerateDirectories(docRoot))
        {
            var db = Path.Combine(wxidDir, "db_storage");
            if (!Directory.Exists(db)) continue;

            var latest = DateTime.MinValue;
            try
            {
                latest = Directory.EnumerateFiles(db, "*.db", SearchOption.AllDirectories)
                    .Select(f => { try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; } })
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
            }
            catch (Exception)
            {
                continue;
            }

            if (latest > bestTime)
            {
                bestTime = latest;
                best = db;
            }
        }
        return best ?? "";
    }
}