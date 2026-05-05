namespace ChatClient.Web.Models
{
    public class ChatRoomItem
    {
        public string Name { get; set; } = string.Empty;
        public bool IsPrivate { get; set; }

        /// <summary>Username who created the room. Empty for system rooms (General, Lobby).</summary>
        public string CreatedBy { get; set; } = string.Empty;
    }
}
