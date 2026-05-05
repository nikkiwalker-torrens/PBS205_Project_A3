using System.Collections.Concurrent;
using ChatClient.Web.Models;
using Microsoft.Extensions.Options;

namespace ChatClient.Web.Services;

public partial class ChatStateService : IChatStateService
{
    private static readonly TimeSpan OnlineThreshold = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, HashSet<string>> _roomUsers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, DateTime> _lastSeen =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<ChatHistoryItem>> _history =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, HashSet<string>> _privateRoomInvites =
        new(StringComparer.OrdinalIgnoreCase);

    // Key: "username::room", Value: unread count
    private readonly ConcurrentDictionary<string, int> _unreadCounts =
        new(StringComparer.OrdinalIgnoreCase);

    // Rooms that have been explicitly deleted this session — prevents them
    // from being resurrected by heartbeats or reconnecting clients.
    private readonly ConcurrentDictionary<string, byte> _deletedRooms =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly RabbitMqOptions _rabbitMqOptions;
    private readonly ChatPersistenceService _persistence;
    private IRabbitMqChatService? _rabbitMqChatService;

    public ChatStateService(
        IOptions<RabbitMqOptions> rabbitMqOptions,
        ChatPersistenceService persistence)
    {
        _rabbitMqOptions = rabbitMqOptions.Value;
        _persistence = persistence;

        _roomUsers["General"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _history["General"] = new List<ChatHistoryItem>();
    }

    public void SetRabbitMqService(IRabbitMqChatService rabbitMqChatService)
    {
        _rabbitMqChatService = rabbitMqChatService;
    }

    // -------------------------------------------------------------------------
    // Startup  called once from Program.cs after DB is ready
    // -------------------------------------------------------------------------

    public async Task LoadFromDatabaseAsync()
    {
        // Rooms
        var rooms = await _persistence.LoadAllRoomsAsync();
        foreach (var (name, isPrivate) in rooms)
        {
            _roomUsers.TryAdd(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _history.TryAdd(name, new List<ChatHistoryItem>());
            if (isPrivate)
                _privateRoomInvites.TryAdd(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        // Private room members
        var members = await _persistence.LoadAllPrivateMembersAsync();
        foreach (var (room, username) in members)
        {
            var invited = _privateRoomInvites.GetOrAdd(
                room, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (invited) { invited.Add(username); }

            var roomUsers = _roomUsers.GetOrAdd(
                room, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (roomUsers) { roomUsers.Add(username); }
        }

        // Recover any rooms that have messages in the DB but are missing from the
        // Rooms table (e.g. rooms created before persistence was in place).
        var roomsWithMessages = await _persistence.GetRoomNamesFromMessagesAsync();
        foreach (var roomName in roomsWithMessages)
        {
            if (string.IsNullOrWhiteSpace(roomName)) continue;
            if (_roomUsers.TryAdd(roomName, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                _history.TryAdd(roomName, new List<ChatHistoryItem>());
                // Re-save to Rooms table so it persists properly going forward
                await _persistence.SaveRoomAsync(roomName, isPrivate: false);
            }
        }

        // Message history per room
        foreach (var roomName in _roomUsers.Keys.ToList())
        {
            var history = await _persistence.LoadRoomHistoryAsync(roomName);
            var list = _history.GetOrAdd(roomName, _ => new List<ChatHistoryItem>());
            lock (list)
            {
                list.Clear();
                list.AddRange(history);
            }
        }

        // Restore unread counts from DB so they survive server restarts.
        // Counts are derived by comparing each user's LastReadUtc against
        // message timestamps � no extra data needs to be stored beyond what
        // already exists in the Messages and UserLastReads tables.
        foreach (var roomName in _roomUsers.Keys.ToList())
        {
            var counts = await _persistence.LoadUnreadCountsForRoomAsync(roomName);
            foreach (var (username, count) in counts)
            {
                if (count > 0)
                {
                    var key = username + "::" + roomName;
                    _unreadCounts[key] = count;
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Rooms
    // -------------------------------------------------------------------------

    public Task<List<ChatRoomItem>> GetRoomsAsync()
    {
        var rooms = GetAllRoomNames()
            .Select(roomName => new ChatRoomItem
            {
                Name = roomName,
                IsPrivate = IsRoomPrivate(roomName)
            })
            .ToList();

        return Task.FromResult(rooms);
    }

    public IReadOnlyList<string> GetAllRoomNames()
    {
        var rooms = _roomUsers.Keys.ToList();

        if (!rooms.Any(r => r.Equals("General", StringComparison.OrdinalIgnoreCase)))
            rooms.Insert(0, "General");

        return rooms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r == "General" ? 0 : 1)
            .ThenBy(r => r)
            .ToList();
    }

    public IReadOnlyList<string> GetRoomsForUser(string username)
    {
        var result = new List<string> { "General" };

        foreach (var kvp in _roomUsers)
        {
            if (string.Equals(kvp.Key, "General", StringComparison.OrdinalIgnoreCase))
                continue;

            if (CanUserAccessRoom(username, kvp.Key))
                result.Add(kvp.Key);
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r == "General" ? 0 : 1)
            .ThenBy(r => r)
            .ToList();
    }

    public void CreateRoom(string roomName, string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            return;

        roomName = roomName.Trim();

        // Don't recreate a room that was explicitly deleted this session
        if (_deletedRooms.ContainsKey(roomName))
            return;

        _roomUsers.TryAdd(roomName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _history.TryAdd(roomName, new List<ChatHistoryItem>());

        if (!string.IsNullOrWhiteSpace(createdBy))
        {
            var users = _roomUsers.GetOrAdd(roomName, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (users) { users.Add(createdBy.Trim()); }

            AddMessageToHistory(roomName, new ChatHistoryItem
            {
                Room = roomName,
                Username = string.Empty,
                Message = string.Empty,
                TimestampUtc = DateTime.UtcNow,
                ItemType = "status",
                StatusText = $"{createdBy} created the room"
            });
        }
    }

    public async Task CreateRoomAsync(string roomName, bool isPrivate, string createdBy)
    {
        roomName = roomName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(roomName))
            return;

        CreateRoom(roomName, createdBy);

        // Persist the new room
        await _persistence.SaveRoomAsync(roomName, isPrivate, createdBy ?? string.Empty);

        if (isPrivate && !string.IsNullOrWhiteSpace(createdBy))
            InviteUserToRoom(roomName, createdBy);

        if (_rabbitMqChatService != null)
        {
            try
            {
                await _rabbitMqChatService.PublishPresenceAsync(new PresenceMessage
                {
                    Room = roomName,
                    Username = createdBy,
                    Action = "created",
                    TimestampUtc = DateTime.UtcNow
                });
            }
            catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Users / presence
    // -------------------------------------------------------------------------

    public void JoinRoom(string username, string room)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(room))
            return;

        username = username.Trim();
        room = room.Trim();

        // Don't re-add a deleted room
        if (_deletedRooms.ContainsKey(room))
            return;

        CreateRoom(room, null);

        var users = _roomUsers.GetOrAdd(room, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (users) { users.Add(username); }

        _lastSeen[username] = DateTime.UtcNow;
        // NOTE: do NOT call MarkRoomAsRead here. JoinRoom is called on every
        // reconnect for all rooms the user belongs to. Unread counts should
        // only be cleared when the user explicitly opens (views) a room.
    }

    public void LeaveRoom(string username, string room)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(room))
            return;

        username = username.Trim();
        room = room.Trim();

        if (_roomUsers.TryGetValue(room, out var users))
            lock (users) { users.Remove(username); }

        _lastSeen[username] = DateTime.MinValue;
    }

    public IReadOnlyList<string> GetOnlineUsers(string room)
    {
        return GetRoomMembers(room)
            .Where(IsUserOnline)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetRoomMembers(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return [];

        room = room.Trim();

        if (!_roomUsers.TryGetValue(room, out var users))
            return [];

        List<string> members;
        lock (users) { members = users.ToList(); }

        if (IsRoomPrivate(room))
        {
            if (_privateRoomInvites.TryGetValue(room, out var invitedUsers))
            {
                HashSet<string> invitedCopy;
                lock (invitedUsers)
                {
                    invitedCopy = new HashSet<string>(invitedUsers, StringComparer.OrdinalIgnoreCase);
                }
                members = members.Where(u => invitedCopy.Contains(u)).ToList();
            }
            else
            {
                members.Clear();
            }
        }

        return members
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int GetOnlineUserCount(string room) => GetOnlineUsers(room).Count;

    public Task<int> GetOnlineUserCountAsync(string room) =>
        Task.FromResult(GetOnlineUserCount(room));

    public bool IsUserOnline(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        if (!_lastSeen.TryGetValue(username.Trim(), out var lastSeen))
            return false;

        return DateTime.UtcNow - lastSeen <= OnlineThreshold;
    }

    public void UpdateLastSeen(string username, DateTime time)
    {
        if (!string.IsNullOrWhiteSpace(username))
            _lastSeen[username.Trim()] = time;
    }

    public void InviteUserToRoom(string room, string username)
    {
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(username))
            return;

        room = room.Trim();
        username = username.Trim();

        var invitedUsers = _privateRoomInvites.GetOrAdd(
            room, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (invitedUsers) { invitedUsers.Add(username); }

        var roomUsers = _roomUsers.GetOrAdd(
            room, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (roomUsers) { roomUsers.Add(username); }

        _lastSeen.TryAdd(username, DateTime.UtcNow);
        _history.TryAdd(room, new List<ChatHistoryItem>());

        // Fire-and-forget persist  does not block the caller
        _ = _persistence.SavePrivateMemberAsync(room, username);
    }

    public bool CanUserAccessRoom(string username, string room)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(room))
            return false;

        username = username.Trim();
        room = room.Trim();

        if (room.Equals("General", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!_privateRoomInvites.TryGetValue(room, out var invited))
            return true;

        lock (invited) { return invited.Contains(username); }
    }

    public bool IsRoomPrivate(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return false;

        return _privateRoomInvites.ContainsKey(room.Trim());
    }

    // -------------------------------------------------------------------------
    // Messages
    // -------------------------------------------------------------------------

    public async Task SendMessageAsync(string room, string username, string message)
    {
        if (_rabbitMqChatService == null)
            throw new InvalidOperationException("RabbitMQ service has not been attached to ChatStateService.");

        await _rabbitMqChatService.PublishMessageAsync(new ChatMessage
        {
            Room = string.IsNullOrWhiteSpace(room) ? "General" : room.Trim(),
            Username = username?.Trim() ?? string.Empty,
            Message = message?.Trim() ?? string.Empty,
            TimestampUtc = DateTime.UtcNow
        });
    }

    public async Task SendReplyAsync(ChatMessage message)
    {
        if (_rabbitMqChatService == null)
            throw new InvalidOperationException("RabbitMQ service has not been attached to ChatStateService.");
        message.Room = string.IsNullOrWhiteSpace(message.Room) ? "General" : message.Room.Trim();
        message.TimestampUtc = DateTime.UtcNow;
        await _rabbitMqChatService.PublishMessageAsync(message);
    }

    public void AddMessageToHistory(string room, ChatHistoryItem message)
    {
        if (string.IsNullOrWhiteSpace(room) || message == null)
            return;

        room = room.Trim();
        message.Room = room;

        var history = _history.GetOrAdd(room, _ => new List<ChatHistoryItem>());

        lock (history)
        {
            history.Add(message);

            // Keep in-memory cap at 200; DB holds the full 500
            if (history.Count > 200)
                history.RemoveAt(0);
        }

        // NOTE: persistence is handled by RabbitMqChatService.HandleDeliveryAsync,
        // which is the single point where every message (from any instance) arrives.
        // Writing here too would cause duplicates under concurrent multi-instance load.

        // Only count chat messages for unread badges, not status events
        if (!string.Equals(message.ItemType, "message", StringComparison.OrdinalIgnoreCase))
            return;

        // Increment for every member of the room except the sender.
        // message.Username is the display name; _roomUsers stores login usernames.
        // Resolve both so we correctly skip the sender regardless of which form is stored.
        var senderDisplayName = message.Username ?? string.Empty;
        var senderLoginName   = ChatClient.Web.Hubs.ChatHub.GetLoginUsernameByDisplayName(senderDisplayName);

        foreach (var username in GetRoomMembers(room))
        {
            // Skip if this member is the sender (matched by login name OR display name)
            if (string.Equals(username, senderDisplayName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(senderLoginName) &&
                string.Equals(username, senderLoginName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip if this member currently has the room open — message is already visible
            var currentRoom = ChatClient.Web.Hubs.ChatHub.GetCurrentRoom(username);
            if (string.Equals(currentRoom, room, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = username + "::" + room;
            _unreadCounts.AddOrUpdate(key, 1, (_, existing) => existing + 1);
        }
    }

    public Task<List<WebChatMessage>> GetMessagesAsync(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return Task.FromResult(new List<WebChatMessage>());

        room = room.Trim();

        if (!_history.TryGetValue(room, out var items))
            return Task.FromResult(new List<WebChatMessage>());

        lock (items)
        {
            var messages = items
                .Where(x => string.Equals(x.ItemType, "message", StringComparison.OrdinalIgnoreCase))
                .Select(x => new WebChatMessage
                {
                    Username = x.Username,
                    Text = x.Message,
                    Timestamp = x.TimestampUtc.ToLocalTime()
                })
                .ToList();

            return Task.FromResult(messages);
        }
    }

    public IReadOnlyList<ChatHistoryItem> GetVisibleRoomHistory(string room, string username)
    {
        if (!CanUserAccessRoom(username, room))
            return [];

        if (_history.TryGetValue(room, out var items))
            lock (items) { return items.ToList(); }

        return [];
    }

    // -------------------------------------------------------------------------
    // Unread counts
    // -------------------------------------------------------------------------

    public IReadOnlyDictionary<string, int> GetUnreadCounts(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var prefix = username.Trim() + "::";
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _unreadCounts)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var room = kvp.Key.Substring(prefix.Length);
                if (kvp.Value > 0)
                    result[room] = kvp.Value;
            }
        }

        return result;
    }

    public void MarkRoomAsRead(string room, string username)
    {
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(username))
            return;
        room = room.Trim();
        username = username.Trim();
        var key = username + "::" + room;
        _unreadCounts[key] = 0;
        // Persist the read timestamp so unread counts survive server restarts.
        _ = _persistence.SaveLastReadAsync(username, room, DateTime.UtcNow);
    }

    // -------------------------------------------------------------------------
    // Status / connection info
    // -------------------------------------------------------------------------

    public Task<bool> IsConnectedAsync()
    {
        if (_rabbitMqChatService is RabbitMqChatService rabbit)
            return Task.FromResult(rabbit.IsConnected);

        return Task.FromResult(false);
    }

    public ChatConnectionInfo GetConnectionInfo()
    {
        return new ChatConnectionInfo
        {
            IpAddress = string.IsNullOrWhiteSpace(_rabbitMqOptions.HostName)
                ? "Unknown"
                : _rabbitMqOptions.HostName,
            Port = _rabbitMqOptions.Port
        };
    }
}