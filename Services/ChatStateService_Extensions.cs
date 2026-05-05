using ChatClient.Web.Models;

namespace ChatClient.Web.Services;

/// <summary>
/// Partial extension of ChatStateService implementing DeleteRoomAsync
/// from IChatStateService.
///
/// SETUP: Add the keyword "partial" to the class declaration in your
/// existing ChatStateService.cs file:
///
///     public partial class ChatStateService : IChatStateService { ... }
///
/// Then drop this file into the same Services/ folder.
/// </summary>
public partial class ChatStateService
{
    public async Task<bool> DeleteRoomAsync(string roomName, string requestingUser)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return false;

        var ok = await _persistence.DeleteRoomAsync(roomName, requestingUser);
        if (!ok) return false;

        // Evict all members and clear in-memory history so the room
        // vanishes immediately without requiring a restart.
        var members = GetRoomMembers(roomName).ToList();
        foreach (var member in members)
            LeaveRoom(member, roomName);

        _roomUsers.TryRemove(roomName, out _);
        _history.TryRemove(roomName, out _);
        _privateRoomInvites.TryRemove(roomName, out _);

        // Mark as deleted so heartbeats/reconnects can't resurrect it
        _deletedRooms.TryAdd(roomName, 0);

        // Clean up unread count entries for this room
        var keysToRemove = _unreadCounts.Keys
            .Where(k => k.EndsWith("::" + roomName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keysToRemove)
            _unreadCounts.TryRemove(key, out _);

        return true;
    }

    public void ClearRoomHistory(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;
        if (_history.TryGetValue(roomName, out var hist))
            lock (hist) { hist.Clear(); }
    }
}
