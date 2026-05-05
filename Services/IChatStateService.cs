using ChatClient.Web.Models;

namespace ChatClient.Web.Services;

public interface IChatStateService
{
    Task<List<ChatRoomItem>> GetRoomsAsync();
    IReadOnlyList<string> GetAllRoomNames();
    IReadOnlyList<string> GetRoomsForUser(string username);

    void CreateRoom(string roomName, string? createdBy);
    Task CreateRoomAsync(string roomName, bool isPrivate, string createdBy);

    void JoinRoom(string username, string room);
    void LeaveRoom(string username, string room);

    IReadOnlyList<string> GetOnlineUsers(string room);
    IReadOnlyList<string> GetRoomMembers(string room);
    int GetOnlineUserCount(string room);
    Task<int> GetOnlineUserCountAsync(string room);
    bool IsUserOnline(string username);

    void UpdateLastSeen(string username, DateTime time);
    void InviteUserToRoom(string room, string username);

    bool CanUserAccessRoom(string username, string room);
    bool IsRoomPrivate(string room);

    /// <summary>Deletes a room if the requesting user is its creator. Returns false if denied or not found.</summary>
    Task<bool> DeleteRoomAsync(string roomName, string requestingUser);

    /// <summary>Clears the in-memory message history for a room (admin use).</summary>
    void ClearRoomHistory(string roomName);

    Task SendMessageAsync(string room, string username, string message);
    Task SendReplyAsync(ChatClient.Web.Models.ChatMessage message);
    void AddMessageToHistory(string room, ChatHistoryItem message);
    Task<List<WebChatMessage>> GetMessagesAsync(string room);

    IReadOnlyList<ChatHistoryItem> GetVisibleRoomHistory(string room, string username);
    IReadOnlyDictionary<string, int> GetUnreadCounts(string username);
    void MarkRoomAsRead(string room, string username);

    Task<bool> IsConnectedAsync();
    ChatConnectionInfo GetConnectionInfo();
}