using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WxToastNotifier.Models;

namespace WxToastNotifier.Services;

/// <summary>
/// 通过 WinRT 的 ToastNotificationManager 把消息推送到系统通知中心（Toast）。
/// 与 PowerToys LightSwitch 同款实现：MSIX 打包下无需 app 通知 COM server 注册。
/// </summary>
public sealed class ToastService
{
    public bool IsRegistered => true;
    public string? LastError { get; private set; }

    /// <summary>ToastNotificationManager 即时触发，无需预先注册；保留此方法以兼容调用方。</summary>
    public void Register()
    {
        LastError = null;
    }

    public void Show(NewMessage msg, string? avatarPath = null)
    {
        try
        {
            var title = string.IsNullOrWhiteSpace(msg.DisplayName) ? "微信消息" : msg.DisplayName;
            var content = msg.Content ?? "";

            Windows.Data.Xml.Dom.XmlDocument xml;
            var src = string.IsNullOrEmpty(avatarPath) ? null : ToImageSrc(avatarPath);
            if (src != null)
            {
                // ToastGeneric + 圆形裁剪头像
                xml = new Windows.Data.Xml.Dom.XmlDocument();
                xml.LoadXml(
                    "<toast><visual><binding template=\"ToastGeneric\">" +
                    $"<text>{System.Security.SecurityElement.Escape(title)}</text>" +
                    $"<text>{System.Security.SecurityElement.Escape(content)}</text>" +
                    $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{System.Security.SecurityElement.Escape(src)}\"/>" +
                    "</binding></visual></toast>");
            }
            else
            {
                xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(title));
                texts[1].AppendChild(xml.CreateTextNode(content));
            }

            var toast = new ToastNotification(xml);
            ToastNotificationManager.CreateToastNotifier().Show(toast);
            LastError = null;
            LastUsedAvatarSrc = src;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>最近一次通知是否带上了头像图源（null 表示未使用头像），便于日志排查。</summary>
    public string? LastUsedAvatarSrc { get; private set; }

    /// <summary>
    /// 打包应用的 Toast 图片只支持 ms-appx / ms-appdata / http(s)，file:/// 会被静默丢弃。
    /// 缓存放在 LocalFolder 下时转换为 ms-appdata:///local/...；否则退回 file://（非打包场景）。
    /// </summary>
    private static string? ToImageSrc(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var localRoot = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (path.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase))
            {
                var rel = path[localRoot.Length..].TrimStart('\\', '/').Replace('\\', '/');
                return "ms-appdata:///local/" + rel;
            }
        }
        catch
        {
            // 非打包运行时没有 ApplicationData，走 file:// 兜底
        }

        return new Uri(path).AbsoluteUri;
    }
}