using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WxToastNotifier.Models;

namespace WxToastNotifier.Services;

/// <summary>
/// 通过 Windows App SDK 的 AppNotification 把消息推送到系统通知中心（Toast）。
/// </summary>
public sealed class ToastService
{
    public bool IsRegistered { get; private set; }
    public string? LastError { get; private set; }

    public void Register()
    {
        if (IsRegistered) return;
        try
        {
            AppNotificationManager.Default.Register();
            IsRegistered = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void Show(NewMessage msg)
    {
        Register();
        if (!IsRegistered) return;

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("username", msg.Username)
                .AddText(msg.DisplayName)
                .AddText(msg.Content);

            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}