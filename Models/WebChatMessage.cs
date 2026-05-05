using System;

namespace ChatClient.Web.Models
{
    public class WebChatMessage
    {
        public string Username { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>True when this entry is a status/system event (join, leave, invite) rather than a user message.</summary>
        public bool IsStatus { get; set; }

        /// <summary>Human-readable description of the status event, e.g. "Alice joined the room".</summary>
        public string StatusText { get; set; } = string.Empty;
    }
}
