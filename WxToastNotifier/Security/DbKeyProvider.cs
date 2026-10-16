using System.Text.Json;
using WxToastNotifier.Models;

namespace WxToastNotifier.Security;

/// <summary>数据库密钥集：按相对路径索引每个 .db 的 enc_key。</summary>
public sealed record KeySet(IReadOnlyDictionary<string, byte[]> KeysByRelPath)
{
    public byte[]? For(string relPath) => KeysByRelPath.TryGetValue(relPath, out var k) ? k : null;
}

/// <summary>
/// 解析微信数据库密钥：优先读取既有 all_keys.json，否则由配置的 passphrase 派生并校验。
/// </summary>
public static class DbKeyProvider
{
    /// <summary>收集 db_storage 下所有 .db 文件（排除 -wal/-shm），按相对路径返回。</summary>
    public static List<string> CollectDbFiles(string dbStorage)
    {
        var result = new List<string>();
        if (!Directory.Exists(dbStorage)) return result;

        foreach (var path in Directory.EnumerateFiles(dbStorage, "*.db", SearchOption.AllDirectories))
        {
            if (path.EndsWith("-wal") || path.EndsWith("-shm")) continue;
            if (new FileInfo(path).Length < WeChatCrypto.PageSize) continue;
            result.Add(Path.GetRelativePath(dbStorage, path));
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public static KeySet Resolve(string dbStorage, AppConfig config, Action<string>? log = null)
    {
        var dbs = CollectDbFiles(dbStorage);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // 1. 微信 4.1+ 主路径：WCDB Config.Cipher 内存扫描（无需 passphrase / 密钥文件）
        try
        {
            var scanned = WeChatKeyExtractor.Extract(dbStorage, log);
            foreach (var (rel, key) in scanned)
                result[rel] = key;
        }
        catch (Exception ex)
        {
            log?.Invoke($"⚠️ 内存扫描失败：{ex.Message}");
        }

        // 2. 剩余数据库用既有 all_keys.json 补齐
        if (!string.IsNullOrWhiteSpace(config.KeysFile) && File.Exists(config.KeysFile))
        {
            LoadFromKeysJson(config.KeysFile, dbs, result);
        }

        // 3. 剩余数据库用 passphrase 派生（旧版微信回退路径）
        if (!string.IsNullOrWhiteSpace(config.Passphrase))
        {
            LoadFromPassphrase(dbStorage, config.Passphrase, dbs, result);
        }

        return new KeySet(result);
    }

    private static void LoadFromKeysJson(string keysFile, List<string> dbs, Dictionary<string, byte[]> result)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(keysFile));
        var root = doc.RootElement;

        foreach (var rel in dbs)
        {
            if (result.ContainsKey(rel)) continue;
            if (!root.TryGetProperty(rel, out var entry)) continue;
            var encKeyHex = entry.TryGetProperty("enc_key", out var k) ? k.GetString() : null;
            if (string.IsNullOrEmpty(encKeyHex)) continue;
            try
            {
                result[rel] = Convert.FromHexString(encKeyHex);
            }
            catch (FormatException) { }
        }
    }

    private static void LoadFromPassphrase(string dbStorage, string passphrase, List<string> dbs, Dictionary<string, byte[]> result)
    {
        var passphraseBytes = ParsePassphrase(passphrase);
        var keyBySalt = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in dbs)
        {
            if (result.ContainsKey(rel)) continue;

            var fullPath = Path.Combine(dbStorage, rel);
            var salt = WeChatCrypto.ReadSalt(fullPath);
            if (salt.Length != WeChatCrypto.SaltSize) continue;

            var saltHex = Convert.ToHexString(salt);
            if (keyBySalt.TryGetValue(saltHex, out var existing))
            {
                result[rel] = existing;
                continue;
            }

            var encKey = WeChatCrypto.DeriveEncKey(passphraseBytes, salt);
            var page1 = File.ReadAllBytes(fullPath).AsSpan(0, WeChatCrypto.PageSize).ToArray();
            if (WeChatCrypto.VerifyKey(encKey, page1))
            {
                keyBySalt[saltHex] = encKey;
                result[rel] = encKey;
            }
        }
    }

    /// <summary>把配置里的 passphrase 转成 32 字节：接受 64 位 hex 或恰好 32 字节的原文。</summary>
    public static byte[] ParsePassphrase(string passphrase)
    {
        var trimmed = passphrase.Trim();
        if (trimmed.Length == 64)
        {
            try { return Convert.FromHexString(trimmed); }
            catch (FormatException) { }
        }

        var raw = System.Text.Encoding.UTF8.GetBytes(trimmed);
        if (raw.Length == WeChatCrypto.KeySize) return raw;

        throw new InvalidOperationException(
            "passphrase 配置无效：应为 64 位 hex，或恰好 32 字节的原文。");
    }
}