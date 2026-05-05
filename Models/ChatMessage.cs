namespace ChatClient.Web.Models;

public class ChatMessage
{
    public string Room { get; set; } = "";
    public string Username { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    // Reply-to metadata — null when not a reply
    public string? ReplyToUsername { get; set; }
    public string? ReplyToText { get; set; }
}