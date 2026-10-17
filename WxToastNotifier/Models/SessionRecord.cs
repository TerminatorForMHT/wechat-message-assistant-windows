namespace WxToastNotifier.Models;

/// <summary>session.db 中 SessionTable 的一行。</summary>
public sealed record SessionRecord(
    string Username,
    long LastTimestamp,
    int LastMsgType,
    int UnreadCount,
    string? Summary,
    string? LastSender,
    string? LastSenderDisplayName,
    bool Muted);

/// <summary>contact.db 中 contact 表的一行（名称为 remark 优先，其次 nick_name）。</summary>
public sealed record ContactEntry(string Username, string? NickName, string? Remark)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(Remark)
        ? Remark!
        : !string.IsNullOrWhiteSpace(NickName) ? NickName! : Username;
}

/// <summary>一条待推送的新消息通知。</summary>
public sealed record NewMessage(string Username, string DisplayName, string Content, long Timestamp);