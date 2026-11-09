using System.Collections.Concurrent;
using System.Net.Http;
using WxToastNotifier.Models;

namespace WxToastNotifier.Services;

/// <summary>
/// 联系人头像本地缓存：启动时按 head_img_md5 落盘刷新一次映射，
/// 缺失的头像后台并发下载；推送路径只做 O(1) 字典查询，不影响推送速度。
/// </summary>
public sealed class AvatarService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly string _cacheDir = Path.Combine(CacheRoot(), "avatars");
    private readonly Action<string> _log;

    /// <summary>
    /// 头像缓存根目录。打包应用必须放在 ApplicationData.LocalFolder（LocalState）下，
    /// 这样 Toast 才能用 ms-appdata:///local/ 引用；LocalCache\Local 无法被 ms-appdata 访问。
    /// </summary>
    private static string CacheRoot()
    {
        try { return Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WxToastNotifier"); }
    }

    public string CacheDirectory => _cacheDir;

    /// <summary>username → 头像文件路径（仅缓存命中的入表）。</summary>
    private ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);

    public AvatarService(Action<string> log) => _log = log;

    public string? TryGet(string username) =>
        _map.TryGetValue(username, out var p) ? p : null;

    /// <summary>刷新 username→头像 映射；未缓存的后台下载，完成后自动入表。不阻塞调用方。</summary>
    public void Refresh(IEnumerable<ContactEntry> contacts)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var map = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var toDownload = new List<(string User, string Url, string Path)>();

            foreach (var c in contacts)
            {
                if (string.IsNullOrEmpty(c.Username) || string.IsNullOrEmpty(c.SmallHeadUrl)) continue;

                // head_img_md5 常为空，用 URL 的 md5 兜底（头像更换时 URL 会变，自然失效重下）
                var key = !string.IsNullOrEmpty(c.HeadImgMd5) ? c.HeadImgMd5! : Md5Hex(c.SmallHeadUrl);
                var path = Path.Combine(_cacheDir, key + ".jpg");
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    map[c.Username] = path;
                else
                    toDownload.Add((c.Username, c.SmallHeadUrl, path));
            }

            _map = map;
            _log($"✓ 头像缓存：{map.Count} 个本地可用，{toDownload.Count} 个后台下载");

            if (toDownload.Count > 0)
                _ = Task.Run(() => DownloadAll(toDownload));
        }
        catch (Exception ex)
        {
            _log($"⚠️ 头像缓存刷新失败：{ex.Message}");
        }
    }

    private static string Md5Hex(string s)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task DownloadAll(List<(string User, string Url, string Path)> items)
    {
        using var sem = new SemaphoreSlim(4);
        var tasks = items.Select(async it =>
        {
            await sem.WaitAsync();
            try
            {
                var bytes = await Http.GetByteArrayAsync(it.Url);
                if (bytes.Length > 0)
                {
                    var tmp = it.Path + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes);
                    File.Move(tmp, it.Path, true);
                    _map[it.User] = it.Path;
                }
            }
            catch (Exception)
            {
                // 单个头像失败不影响整体推送
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        _log($"✓ 头像下载完成，当前共 {_map.Count} 个可用");
    }
}
