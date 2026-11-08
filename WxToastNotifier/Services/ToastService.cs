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
            if (!string.IsNullOrEmpty(avatarPath) && File.Exists(avatarPath))
            {
                // ToastGeneric + 圆形裁剪头像
                xml = new Windows.Data.Xml.Dom.XmlDocument();
                var src = System.Security.SecurityElement.Escape(new Uri(avatarPath).AbsoluteUri) ?? "";
                xml.LoadXml(
                    "<toast><visual><binding template=\"ToastGeneric\">" +
                    $"<text>{System.Security.SecurityElement.Escape(title)}</text>" +
                    $"<text>{System.Security.SecurityElement.Escape(content)}</text>" +
                    $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{src}\"/>" +
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
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}