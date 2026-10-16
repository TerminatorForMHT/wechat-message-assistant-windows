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

    public static string TryAutoDetectDbStorage()
    {
        var docRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "xwechat_files");
        if (!Directory.Exists(docRoot)) return "";

        foreach (var wxidDir in Directory.EnumerateDirectories(docRoot))
        {
            var db = Path.Combine(wxidDir, "db_storage");
            if (Directory.Exists(db) &&
                Directory.EnumerateFiles(db, "*.db", SearchOption.AllDirectories).Any())
                return db;
        }
        return "";
    }
}