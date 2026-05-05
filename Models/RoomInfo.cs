namespace ChatClient.Web.Models;

public class RoomInfo
{
    public string Name { get; set; } = "";
    public bool IsPrivate { get; set; }
    public string Owner { get; set; } = "";

    public HashSet<string> Members { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> InvitedUsers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}