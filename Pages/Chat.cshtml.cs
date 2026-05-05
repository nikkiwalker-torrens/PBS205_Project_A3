using System.Text;
using System.Text.Json;
using ChatClient.Web.Hubs;
using ChatClient.Web.Models;
using ChatClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;

namespace ChatClient.Web.Pages;

[Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
public class ChatModel : PageModel
{
    private readonly IChatStateService _chatStateService;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ChatPersistenceService _persistence;

    public ChatModel(IChatStateService chatStateService, IHubContext<ChatHub> hubContext, ChatPersistenceService persistence)
    {
        _chatStateService = chatStateService;
        _hubContext = hubContext;
        _persistence = persistence;
    }

    public List<ChatRoomItem> Rooms { get; set; } = new();
    public List<WebChatMessage> Messages { get; set; } = new();
    public List<string> Members { get; set; } = new();
    public List<string> InvitableUsers { get; set; } = new();
    public IReadOnlyDictionary<string, int> UnreadCounts { get; set; } = new Dictionary<string, int>();

    [BindProperty] public string MessageText { get; set; } = string.Empty;
    [BindProperty] public string NewRoomName { get; set; } = string.Empty;
    [BindProperty] public bool NewRoomIsPrivate { get; set; }
    [BindProperty] public string InviteUsername { get; set; } = string.Empty;

    public string Username { get; set; } = "Guest";
    public string DisplayName { get; set; } = "Guest";
    public string CurrentRoom { get; set; } = "General";
    public int OnlineUserCount { get; set; }
    public bool IsRabbitConnected { get; set; }
    public string IpAddress { get; set; } = "Unknown";
    public int Port { get; set; }
    public bool IsCurrentRoomPrivate { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }

    // -------------------------------------------------------------------------
    // GET
    // -------------------------------------------------------------------------

    public async Task<IActionResult> OnGetAsync(string? room = null)
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        LoadIdentity();
        IsAdmin = await _persistence.IsAdminAsync(Username);

        var sessionRoom = HttpContext.Session.GetString("CurrentRoom");

        if (!string.IsNullOrWhiteSpace(room))
            CurrentRoom = room.Trim();
        else if (!string.IsNullOrWhiteSpace(sessionRoom))
            CurrentRoom = sessionRoom;
        else
            CurrentRoom = "General";

        if (!_chatStateService.CanUserAccessRoom(Username, CurrentRoom))
            CurrentRoom = "General";

        HttpContext.Session.SetString("CurrentRoom", CurrentRoom);
        _chatStateService.JoinRoom(Username, CurrentRoom);

        _chatStateService.UpdateLastSeen(Username, DateTime.UtcNow);

        await LoadPageDataAsync();
        return Page();
    }

    // -------------------------------------------------------------------------
    // Beacon (tab close / navigate away)
    // -------------------------------------------------------------------------

    public async Task<IActionResult> OnPostBeaconAsync()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();

            if (!string.IsNullOrWhiteSpace(body))
            {
                var payload = JsonSerializer.Deserialize<BeaconPayload>(
                    body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (payload != null && !string.IsNullOrWhiteSpace(payload.Username))
                {
                    var user = payload.Username.Trim();
                    var beaconRoom = string.IsNullOrWhiteSpace(payload.Room) ? "General" : payload.Room.Trim();

                    _chatStateService.UpdateLastSeen(user, DateTime.MinValue);
                    await BroadcastPresenceAsync(beaconRoom);
                }
            }
        }
        catch
        {
            // Beacon must never error — browser does not retry
        }

        return new OkResult();
    }

    // -------------------------------------------------------------------------
    // POST handlers
    // -------------------------------------------------------------------------

    public IActionResult OnPostJoinRoom(string roomName)
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        LoadIdentity();

        if (string.IsNullOrWhiteSpace(roomName))
            return RedirectToPage(new { room = "General" });

        roomName = roomName.Trim();

        if (!_chatStateService.CanUserAccessRoom(Username, roomName))
            return RedirectToPage(new { room = "General" });

        HttpContext.Session.SetString("CurrentRoom", roomName);
        _chatStateService.JoinRoom(Username, roomName);

        return RedirectToPage(new { room = roomName });
    }

    public async Task<IActionResult> OnPostSendMessageAsync()
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        LoadIdentity();

        CurrentRoom = HttpContext.Session.GetString("CurrentRoom") ?? "General";

        if (!_chatStateService.CanUserAccessRoom(Username, CurrentRoom))
        {
            CurrentRoom = "General";
            HttpContext.Session.SetString("CurrentRoom", CurrentRoom);
        }

        if (!string.IsNullOrWhiteSpace(MessageText))
            await _chatStateService.SendMessageAsync(CurrentRoom, Username, MessageText.Trim());

        return RedirectToPage(new { room = CurrentRoom });
    }

    public async Task<IActionResult> OnPostCreateRoomAsync()
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        LoadIdentity();

        CurrentRoom = HttpContext.Session.GetString("CurrentRoom") ?? "General";

        var roomName = NewRoomName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(roomName))
        {
            StatusMessage = "Enter a room name.";
            await LoadPageDataAsync();
            return Page();
        }

        var exists = _chatStateService
            .GetAllRoomNames()
            .Any(r => string.Equals(r, roomName, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            StatusMessage = "That room already exists.";
            await LoadPageDataAsync();
            return Page();
        }

        await _chatStateService.CreateRoomAsync(roomName, NewRoomIsPrivate, Username);
        HttpContext.Session.SetString("CurrentRoom", roomName);
        _chatStateService.JoinRoom(Username, roomName);

        // Broadcast new public room to all connected users immediately
        if (!NewRoomIsPrivate)
            await ChatClient.Web.Hubs.ChatHub.BroadcastRoomsToAllAsync(_hubContext, _chatStateService);

        return RedirectToPage(new { room = roomName });
    }

    public IActionResult OnPostLeaveRoom()
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        LoadIdentity();

        var room = HttpContext.Session.GetString("CurrentRoom") ?? "General";
        _chatStateService.LeaveRoom(Username, room);

        HttpContext.Session.SetString("CurrentRoom", "General");
        _chatStateService.JoinRoom(Username, "General");

        return RedirectToPage(new { room = "General" });
    }

    public async Task<IActionResult> OnPostExportChatAsync()
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        LoadIdentity();

        CurrentRoom = HttpContext.Session.GetString("CurrentRoom") ?? "General";

        if (!_chatStateService.CanUserAccessRoom(Username, CurrentRoom))
            CurrentRoom = "General";

        var messages = await _chatStateService.GetMessagesAsync(CurrentRoom);

        var sb = new StringBuilder();
        sb.AppendLine($"Room: {CurrentRoom}");
        sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var m in messages)
            sb.AppendLine($"[{m.Timestamp:yyyy-MM-dd HH:mm:ss}] {m.Username}: {m.Text}");

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain", $"{CurrentRoom}_chat_export.txt");
    }

    public async Task<IActionResult> OnPostInviteUserAsync()
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        LoadIdentity();

        CurrentRoom = HttpContext.Session.GetString("CurrentRoom") ?? "General";

        if (!_chatStateService.CanUserAccessRoom(Username, CurrentRoom))
        {
            CurrentRoom = "General";
            HttpContext.Session.SetString("CurrentRoom", CurrentRoom);
        }

        if (!_chatStateService.IsRoomPrivate(CurrentRoom))
        {
            StatusMessage = "Only private rooms can have invited users.";
            await LoadPageDataAsync();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(InviteUsername))
        {
            StatusMessage = "Enter a username to add.";
            await LoadPageDataAsync();
            return Page();
        }

        var invitedUser = InviteUsername.Trim();

        if (string.Equals(invitedUser, Username, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "You are already in this room.";
            await LoadPageDataAsync();
            return Page();
        }

        _chatStateService.InviteUserToRoom(CurrentRoom, invitedUser);
        _chatStateService.AddMessageToHistory(CurrentRoom, new ChatHistoryItem
        {
            Room = CurrentRoom,
            Username = string.Empty,
            Message = string.Empty,
            TimestampUtc = DateTime.UtcNow,
            ItemType = "status",
            StatusText = $"{invitedUser} was invited to the room"
        });

        // Push updated room list to the invited user immediately
        var inviteeConnectionIds = ChatHub.GetConnectionIds(invitedUser);
        if (inviteeConnectionIds.Count > 0)
        {
            var roomsForInvitee = _chatStateService
                .GetRoomsForUser(invitedUser)
                .Select(r => new { name = r, isPrivate = _chatStateService.IsRoomPrivate(r) })
                .ToList();
            await _hubContext.Clients
                .Clients(inviteeConnectionIds)
                .SendAsync("RoomsUpdated", roomsForInvitee);
        }

        // Also push updated room list to all users in the current room
        // so the members panel and presence stay in sync
        var currentRoomConnections = ChatHub.GetConnectionIds(Username);
        if (currentRoomConnections.Count > 0)
        {
            var roomsForInviter = _chatStateService
                .GetRoomsForUser(Username)
                .Select(r => new { name = r, isPrivate = _chatStateService.IsRoomPrivate(r) })
                .ToList();
            await _hubContext.Clients
                .Clients(currentRoomConnections)
                .SendAsync("RoomsUpdated", roomsForInviter);
        }

        StatusMessage = $"{invitedUser} can now access this private room.";
        InviteUsername = string.Empty;

        await LoadPageDataAsync();
        return Page();
    }

    /// <summary>Signs the user out and redirects to the login page.</summary>
    public IActionResult OnPostLogout()
    {
        HttpContext.Session.Clear();
        return RedirectToPage("/Index");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private IActionResult? RequireLogin()
    {
        if (string.IsNullOrWhiteSpace(HttpContext.Session.GetString("Username")))
            return RedirectToPage("/Index");
        return null;
    }

    private async Task LoadPageDataAsync()
    {
        Rooms = _chatStateService
            .GetRoomsForUser(Username)
            .Select(r => new ChatRoomItem
            {
                Name = r,
                IsPrivate = _chatStateService.IsRoomPrivate(r)
            })
            .ToList();

        Messages = await _chatStateService.GetMessagesAsync(CurrentRoom);

        Members = _chatStateService
            .GetRoomMembers(CurrentRoom)
            .Select(m => _chatStateService.IsUserOnline(m) ? m : $"{m} (offline)")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        OnlineUserCount = _chatStateService.GetOnlineUserCount(CurrentRoom);
        IsRabbitConnected = await _chatStateService.IsConnectedAsync();
        IsCurrentRoomPrivate = _chatStateService.IsRoomPrivate(CurrentRoom);

        // Pass server-side unread counts to the view so badges are correct on
        // first load (before SignalR pushes UnreadCountsUpdated)
        UnreadCounts = _chatStateService.GetUnreadCounts(Username);

        InvitableUsers = _chatStateService
            .GetAllRoomNames()
            .SelectMany(r => _chatStateService.GetRoomMembers(r))
            .Where(u => !string.Equals(u, Username, StringComparison.OrdinalIgnoreCase))
            .Where(u => _chatStateService.IsUserOnline(u))
            .Where(u => !_chatStateService.CanUserAccessRoom(u, CurrentRoom))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var endpoint = _chatStateService.GetConnectionInfo();
        IpAddress = endpoint.IpAddress;
        Port = endpoint.Port;
    }

    private void LoadIdentity()
    {
        Username = HttpContext.Session.GetString("Username") ?? "Guest";
        var dn = HttpContext.Session.GetString("DisplayName");
        // Fall back to username if display name was never stored in session
        // (users registered before this feature was added)
        DisplayName = string.IsNullOrWhiteSpace(dn) ? Username : dn;
        _chatStateService.UpdateLastSeen(Username, DateTime.UtcNow);
    }

    private async Task BroadcastPresenceAsync(string room)
    {
        var members = _chatStateService
            .GetRoomMembers(room)
            .Select(m => new { username = m, isOnline = _chatStateService.IsUserOnline(m) })
            .ToList();

        await _hubContext.Clients.Group(room).SendAsync("PresenceUpdated", new
        {
            room,
            onlineCount = members.Count(m => m.isOnline),
            members
        });
    }

    private sealed class BeaconPayload
    {
        public string Username { get; set; } = string.Empty;
        public string Room { get; set; } = string.Empty;
    }
}