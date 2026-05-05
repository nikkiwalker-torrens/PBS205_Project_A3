using ChatClient.Web.Models;
using ChatClient.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ChatClient.Web.Hubs;

public class ChatHub : Hub
{
    private readonly IChatStateService _chatStateService;
    private readonly IRabbitMqChatService _rabbitMqChatService;
    private readonly ILogger<ChatHub> _logger;
    private readonly IServiceProvider _serviceProvider;

    private const string UsernameKey = "username";
    private const string DisplayNameKey = "displayName";
    private const string RoomKey = "room";

    // -------------------------------------------------------------------------
    // Current room registry — maps username → the room they currently have open.
    // Updated by OpenRoom so BroadcastPresenceAsync can show correct online status.
    // -------------------------------------------------------------------------
    private static readonly Dictionary<string, string> _currentRooms
        = new(StringComparer.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Display name registry — maps username → display name.
    // Populated when clients call RegisterConnection so BroadcastPresenceAsync
    // can send display names instead of login usernames.
    // -------------------------------------------------------------------------
    private static readonly Dictionary<string, string> _displayNames
        = new(StringComparer.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Connection registry — maps username → set of active connection IDs.
    // Used so the page handler can push a RoomsUpdated event to a specific user
    // when they are invited to a private room, without knowing their connection ID.
    // Static + lock is safe here because ChatHub is transient (new instance per call)
    // but we need the map to outlive individual hub instances.
    // -------------------------------------------------------------------------
    private static readonly Dictionary<string, HashSet<string>> _userConnections
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _registryLock = new();

    public static IReadOnlyList<string> GetConnectionIds(string username)
    {
        lock (_registryLock)
        {
            return _userConnections.TryGetValue(username, out var ids)
                ? ids.ToList()
                : Array.Empty<string>();
        }
    }

    /// <summary>Returns the display name for a username, or the username itself if not registered.</summary>
    public static string GetDisplayName(string username)
    {
        lock (_registryLock)
        {
            return _displayNames.TryGetValue(username, out var dn) && !string.IsNullOrWhiteSpace(dn)
                ? dn
                : username;
        }
    }

    /// <summary>Returns the login username for a given display name, or empty string if not found.</summary>
    public static string GetLoginUsernameByDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return string.Empty;
        lock (_registryLock)
        {
            foreach (var kvp in _displayNames)
            {
                if (string.Equals(kvp.Value, displayName, StringComparison.OrdinalIgnoreCase))
                    return kvp.Key;
            }
            return string.Empty;
        }
    }

    /// <summary>Returns the room a user currently has open, or empty string if not tracked.</summary>
    public static string GetCurrentRoom(string username)
    {
        lock (_registryLock)
        {
            return _currentRooms.TryGetValue(username, out var room) ? room : string.Empty;
        }
    }

    public ChatHub(IChatStateService chatStateService, IRabbitMqChatService rabbitMqChatService, ILogger<ChatHub> logger, IServiceProvider serviceProvider)
    {
        _chatStateService = chatStateService;
        _rabbitMqChatService = rabbitMqChatService;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    // -------------------------------------------------------------------------
    // RegisterConnection — called immediately after the client connects.
    // -------------------------------------------------------------------------

    public async Task RegisterConnection(string username, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        username = username.Trim();
        Context.Items[UsernameKey] = username;

        // Resolve display name — client sends it, but also verify/load from DB
        // so it's correct even if the client session is stale
        var dn = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();
        try
        {
            var persistence = (ChatPersistenceService?)_serviceProvider
                .GetService(typeof(ChatPersistenceService));
            if (persistence != null)
            {
                var dbDn = await persistence.GetDisplayNameAsync(username);
                if (!string.IsNullOrWhiteSpace(dbDn))
                    dn = dbDn;
            }
        }
        catch { }

        Context.Items[DisplayNameKey] = dn;
        lock (_registryLock)
        {
            _displayNames[username] = dn;
        }

        _chatStateService.UpdateLastSeen(username, DateTime.UtcNow);

        // Track this connection ID against the username
        lock (_registryLock)
        {
            if (!_userConnections.TryGetValue(username, out var ids))
            {
                ids = new HashSet<string>();
                _userConnections[username] = ids;
            }
            ids.Add(Context.ConnectionId);
        }

        // Push current RabbitMQ status so the footer is accurate immediately
        var isConnected = await _chatStateService.IsConnectedAsync();
        await Clients.Caller.SendAsync("RabbitStatusUpdated", isConnected);

        // Push unread counts immediately so badges show on the Chat page
        // before the user has opened any room this session.
        await PushUnreadCountsAsync(username);
    }

    // -------------------------------------------------------------------------
    // OpenRoom — join a SignalR group and broadcast updated presence.
    // -------------------------------------------------------------------------

    public async Task OpenRoom(string room, string username)
    {
        room = string.IsNullOrWhiteSpace(room) ? "General" : room.Trim();
        username = string.IsNullOrWhiteSpace(username) ? "Guest" : username.Trim();

        if (!_chatStateService.CanUserAccessRoom(username, room))
            room = "General";

        string? prevRoom = null;
        if (Context.Items.TryGetValue(RoomKey, out var prev) &&
            prev is string pr &&
            !string.Equals(pr, room, StringComparison.OrdinalIgnoreCase))
        {
            prevRoom = pr;
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, prevRoom);
        }

        Context.Items[UsernameKey] = username;
        Context.Items[RoomKey] = room;

        await Groups.AddToGroupAsync(Context.ConnectionId, room);

        // Track the user's current room so presence broadcasts are accurate
        lock (_registryLock)
        {
            _currentRooms[username] = room;
        }

        _chatStateService.JoinRoom(username, room);
        _chatStateService.UpdateLastSeen(username, DateTime.UtcNow);

        await Clients.Caller.SendAsync("RoomOpened", new
        {
            room,
            usersOnline = _chatStateService.GetOnlineUserCount(room)
        });

        // Broadcast presence to new room AND old room so members in both
        // rooms see accurate online status immediately after a room switch.
        await BroadcastPresenceAsync(room);
        if (!string.IsNullOrEmpty(prevRoom))
            await BroadcastPresenceAsync(prevRoom);

        // Push the full room history to the joining client so they see all
        // messages that arrived via RabbitMQ before they connected, including
        // messages from other server instances.
        var history = _chatStateService.GetVisibleRoomHistory(room, username);
        await Clients.Caller.SendAsync("HistoryLoaded", history);

        // Publish to RabbitMQ so instances on other networks update their LastSeen
        // and re-broadcast PresenceUpdated to their own local SignalR clients.
        try
        {
            await _rabbitMqChatService.PublishPresenceAsync(new PresenceMessage
            {
                Room = room,
                Username = username,
                Action = "online",
                TimestampUtc = DateTime.UtcNow
            });
        }
        catch { /* don't break room open if Rabbit is down */ }

        // Send this user's unread counts so the room list badges update
        await PushUnreadCountsAsync(username);
    }

    // -------------------------------------------------------------------------
    // Heartbeat — keeps _lastSeen fresh and pushes live status updates.
    // -------------------------------------------------------------------------

    public async Task Heartbeat(string username, string room)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        username = username.Trim();
        room = string.IsNullOrWhiteSpace(room) ? "General" : room.Trim();

        _chatStateService.UpdateLastSeen(username, DateTime.UtcNow);

        // Use server-tracked current room in case client param is stale after reconnect
        string trackedRoom;
        lock (_registryLock) { _currentRooms.TryGetValue(username, out trackedRoom!); }
        if (!string.IsNullOrEmpty(trackedRoom))
            room = trackedRoom;

        await BroadcastPresenceAsync(room);

        // Publish heartbeat to RabbitMQ so other server instances also refresh
        // this user's LastSeen and push PresenceUpdated to their local clients.
        try
        {
            await _rabbitMqChatService.PublishPresenceAsync(new PresenceMessage
            {
                Room = room,
                Username = username,
                Action = "heartbeat",
                TimestampUtc = DateTime.UtcNow
            });
        }
        catch { /* don't break heartbeat if Rabbit is down */ }

        var isConnected = await _chatStateService.IsConnectedAsync();
        await Clients.Caller.SendAsync("RabbitStatusUpdated", isConnected);
    }

    // -------------------------------------------------------------------------
    // Typing indicators — broadcast to everyone else in the room.
    // -------------------------------------------------------------------------

    // Tracks who is currently typing: room → set of usernames
    private static readonly ConcurrentDictionary<string, HashSet<string>> _typingUsers
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _typingLock = new();

    public async Task StartTyping(string room, string username)
    {
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(username)) return;
        room = room.Trim();
        username = username.Trim();

        _logger.LogInformation("[TYPING] StartTyping called: room={Room} user={User}", room, username);

        bool changed;
        lock (_typingLock)
        {
            var set = _typingUsers.GetOrAdd(room, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            changed = set.Add(username);
        }

        _logger.LogInformation("[TYPING] changed={Changed}, broadcasting={Broadcast}", changed, changed);
        if (changed)
            await BroadcastTypingAsync(room, username);
    }

    public async Task StopTyping(string room, string username)
    {
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(username)) return;
        room = room.Trim();
        username = username.Trim();

        lock (_typingLock)
        {
            if (_typingUsers.TryGetValue(room, out var set))
                set.Remove(username);
        }

        // Always broadcast so clients clear the typing indicator even if the
        // user wasn't in the set (e.g. after a reconnect or rapid send).
        await BroadcastTypingAsync(room, username);
    }

    private async Task BroadcastTypingAsync(string room, string triggeringUser)
    {
        List<string> typers;
        lock (_typingLock)
        {
            typers = _typingUsers.TryGetValue(room, out var set)
                ? set.ToList()
                : new List<string>();
        }
        _logger.LogInformation("[TYPING] Broadcasting to group '{Room}': typers=[{Typers}]", room, string.Join(", ", typers));
        // Map login usernames to display names for the typing indicator
        var typerDisplayNames = typers.Select(t => GetDisplayName(t)).ToList();
        // Send both so JS can filter out the current user by either name
        await Clients.Group(room).SendAsync("TypingUpdated", new { room, typers = typerDisplayNames });
    }

    // -------------------------------------------------------------------------
    // SendMessage — live message send from the composer.
    // -------------------------------------------------------------------------

    public async Task SendMessage(string room, string username, string message)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(message))
            return;

        room = string.IsNullOrWhiteSpace(room) ? "General" : room.Trim();
        username = username.Trim();
        message = message.Trim();

        // username here is the display name (sent by JS). For access checks on
        // private rooms we need the login username — look it up from the registry.
        var loginUsername = GetLoginUsernameByDisplayName(username);
        var accessUsername = string.IsNullOrEmpty(loginUsername) ? username : loginUsername;

        if (!_chatStateService.CanUserAccessRoom(accessUsername, room))
            return;

        // Clear typing indicator for this user when they send
        await StopTyping(room, username);

        _chatStateService.UpdateLastSeen(username, DateTime.UtcNow);

        // Publish to RabbitMQ only. The RabbitMQ consumer (RabbitMqChatService)
        // is the single broadcast path — it calls Clients.Group(room).SendAsync
        // for ALL instances, so broadcasting here too causes every client to
        // receive the message twice.
        // The sender sees their own message immediately via optimistic rendering
        // in chat-heartbeat.js (trySend), so there is no visible delay.
        await _chatStateService.SendMessageAsync(room, username, message);
    }

    // -------------------------------------------------------------------------
    // SendReply — send a message quoting a previous message.
    // -------------------------------------------------------------------------

    public async Task SendReply(string room, string username, string message,
                                string replyToUsername, string replyToText)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(message)) return;
        room = string.IsNullOrWhiteSpace(room) ? "General" : room.Trim();
        username = username.Trim();
        message = message.Trim();
        replyToUsername = replyToUsername?.Trim() ?? string.Empty;
        replyToText = (replyToText?.Trim() ?? string.Empty).Length > 120
            ? replyToText!.Trim()[..120] + "…"
            : (replyToText?.Trim() ?? string.Empty);

        var loginUsernameForReply = GetLoginUsernameByDisplayName(username);
        var accessUsernameForReply = string.IsNullOrEmpty(loginUsernameForReply) ? username : loginUsernameForReply;
        if (!_chatStateService.CanUserAccessRoom(accessUsernameForReply, room)) return;

        await StopTyping(room, username);
        _chatStateService.UpdateLastSeen(username, DateTime.UtcNow);

        await _chatStateService.SendReplyAsync(new ChatClient.Web.Models.ChatMessage
        {
            Room = room,
            Username = username,
            Message = message,
            TimestampUtc = DateTime.UtcNow,
            ReplyToUsername = replyToUsername,
            ReplyToText = replyToText
        });
    }

    // -------------------------------------------------------------------------
    // OpenDm — open or create a direct-message room between two users.
    // -------------------------------------------------------------------------

    public async Task OpenDm(string fromUser, string toUser)
    {
        if (string.IsNullOrWhiteSpace(fromUser) || string.IsNullOrWhiteSpace(toUser)) return;
        fromUser = fromUser.Trim();
        toUser = toUser.Trim();
        if (string.Equals(fromUser, toUser, StringComparison.OrdinalIgnoreCase)) return;

        var parts = new[] { fromUser, toUser }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var dmRoom = $"dm::{parts[0]}::{parts[1]}";

        if (!_chatStateService.GetAllRoomNames()
                .Any(r => string.Equals(r, dmRoom, StringComparison.OrdinalIgnoreCase)))
        {
            await _chatStateService.CreateRoomAsync(dmRoom, isPrivate: true, createdBy: fromUser);
        }

        _chatStateService.InviteUserToRoom(dmRoom, fromUser);
        _chatStateService.InviteUserToRoom(dmRoom, toUser);

        foreach (var user in new[] { fromUser, toUser })
        {
            var ids = GetConnectionIds(user);
            if (ids.Count == 0) continue;
            var rooms = _chatStateService
                .GetRoomsForUser(user)
                .Select(r => new { name = r, isPrivate = _chatStateService.IsRoomPrivate(r) })
                .ToList();
            await Clients.Clients(ids).SendAsync("RoomsUpdated", rooms);
            if (string.Equals(user, fromUser, StringComparison.OrdinalIgnoreCase))
                await Clients.Clients(ids).SendAsync("NavigateToRoom", dmRoom);
        }
    }

    // -------------------------------------------------------------------------
    // BroadcastRoomsToAll — push updated room list to every connected user.
    // Called after a public room is created so it appears immediately for all.
    // -------------------------------------------------------------------------

    public static async Task BroadcastRoomsToAllAsync(
        IHubContext<ChatHub> hubContext,
        IChatStateService chatState)
    {
        List<string> allUsers;
        lock (_registryLock)
        {
            allUsers = _userConnections.Keys.ToList();
        }

        foreach (var user in allUsers)
        {
            var ids = GetConnectionIds(user);
            if (ids.Count == 0) continue;
            var rooms = chatState
                .GetRoomsForUser(user)
                .Select(r => new { name = r, isPrivate = chatState.IsRoomPrivate(r) })
                .ToList();
            await hubContext.Clients.Clients(ids).SendAsync("RoomsUpdated", rooms);
        }
    }

    // -------------------------------------------------------------------------
    // OnDisconnectedAsync — fallback offline marking for crashes / network drops.
    // (The sendBeacon path handles deliberate tab closes faster.)
    // -------------------------------------------------------------------------

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var username = Context.Items.TryGetValue(UsernameKey, out var u) ? u as string : null;
        var room = Context.Items.TryGetValue(RoomKey, out var r) ? r as string : "General";

        if (!string.IsNullOrWhiteSpace(username))
        {
            // Remove from connection registry
            lock (_registryLock)
            {
                if (_userConnections.TryGetValue(username, out var ids))
                {
                    ids.Remove(Context.ConnectionId);
                    if (ids.Count == 0)
                        _userConnections.Remove(username);
                }
            }

            // Remove from any typing sets
            if (!string.IsNullOrWhiteSpace(room))
            {
                lock (_typingLock)
                {
                    if (_typingUsers.TryGetValue(room, out var typers))
                        typers.Remove(username);
                }
                await BroadcastTypingAsync(room, username);
            }

            _chatStateService.UpdateLastSeen(username, DateTime.MinValue);
            lock (_registryLock) { _currentRooms.Remove(username); }

            if (!string.IsNullOrWhiteSpace(room))
            {
                await BroadcastPresenceAsync(room);

                // Publish offline event to RabbitMQ so other server instances mark
                // this user offline and push PresenceUpdated to their local clients.
                try
                {
                    await _rabbitMqChatService.PublishPresenceAsync(new PresenceMessage
                    {
                        Room = room,
                        Username = username,
                        Action = "offline",
                        TimestampUtc = DateTime.UtcNow
                    });
                }
                catch { /* don't break disconnect handling if Rabbit is down */ }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    // -------------------------------------------------------------------------
    // MarkRoomRead — called by the client when they open a room.
    // Tells the server to clear the unread count for that room/user combo.
    // -------------------------------------------------------------------------

    public async Task MarkRoomRead(string room, string username)
    {
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(username))
            return;

        room = room.Trim();
        username = username.Trim();

        _chatStateService.MarkRoomAsRead(room, username);

        // Push the updated (zeroed) counts back to the caller so all their
        // open tabs/windows reflect the change immediately.
        await PushUnreadCountsAsync(username);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pushes the current unread counts for all rooms to the specified user's
    /// connection so the room list badges stay current.
    /// </summary>
    private async Task PushUnreadCountsAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        var counts = _chatStateService.GetUnreadCounts(username.Trim());
        await Clients.Caller.SendAsync("UnreadCountsUpdated", counts);
    }

    private async Task BroadcastPresenceAsync(string room)
    {
        // Use GetRoomMembers for the full list (online + offline members of this room)
        // but only mark as online users whose current tracked room is this one.
        // This prevents users in other rooms showing as Online here.
        var roomMembers = _chatStateService.GetRoomMembers(room);
        var memberList = new List<object>();

        foreach (var m in roomMembers)
        {
            // Display name — already cached in _displayNames by RegisterConnection
            string dn;
            lock (_registryLock) { _displayNames.TryGetValue(m, out dn!); }
            if (string.IsNullOrWhiteSpace(dn)) dn = m; // safe fallback

            // Mark as online if the user is actually online (heartbeat within threshold)
            // regardless of which room they're currently viewing — the member panel
            // shows all room members with their live online status.
            var isOnline = _chatStateService.IsUserOnline(m);

            memberList.Add(new
            {
                username = m,
                displayName = dn,
                isOnline
            });
        }

        var onlineCount = memberList.Count(m => ((dynamic)m).isOnline);

        await Clients.Group(room).SendAsync("PresenceUpdated", new
        {
            room,
            onlineCount,
            members = memberList
        });
    }
}