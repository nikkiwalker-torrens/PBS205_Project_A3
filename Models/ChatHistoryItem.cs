namespace ChatClient.Web.Models;

public class ChatHistoryItem
{
    public string Room { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string TimestampLocal => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string ItemType { get; set; } = "message";
    public string StatusText { get; set; } = string.Empty;

    // Reply-to metadata — null when not a reply
    public string? ReplyToUsername { get; set; }
    public string? ReplyToText { get; set; }
}