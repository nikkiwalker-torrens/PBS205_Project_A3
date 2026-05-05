using ChatClient.Web.Hubs;
using ChatClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using System.Linq;

namespace ChatClient.Web.Pages;

public class AdminModel : PageModel
{
    private readonly ChatPersistenceService _persistence;
    private readonly IChatStateService _chatStateService;

    private readonly IHubContext<ChatHub> _hubContext;

    public AdminModel(ChatPersistenceService persistence, IChatStateService chatStateService, IHubContext<ChatHub> hubContext)
    {
        _persistence = persistence;
        _chatStateService = chatStateService;
        _hubContext = hubContext;
    }

    // ── View model ────────────────────────────────────────────────────────────

    public string AdminUsername { get; set; } = string.Empty;
    public List<UserRow> AllUsers { get; set; } = new();
    public List<RoomRow> AllRooms { get; set; } = new();
    public string? StatusMessage { get; set; }

    public record UserRow(string Username, string DisplayName, DateTime CreatedAt, bool IsAdmin, bool IsOnline);
    public record RoomRow(string Name, bool IsPrivate, bool IsSystem, int MessageCount, int OnlineUsers);

    // ── Guards ────────────────────────────────────────────────────────────────

    private async Task<IActionResult?> RequireAdmin()
    {
        var username = HttpContext.Session.GetString("Username");
        if (string.IsNullOrWhiteSpace(username))
            return RedirectToPage("/Index");

        if (!await _persistence.IsAdminAsync(username))
            return RedirectToPage("/Rooms");

        AdminUsername = username;
        return null;
    }

    // ── Load view model ───────────────────────────────────────────────────────

    private async Task PopulateViewModel()
    {
        var users = await _persistence.GetAllUsersAsync();
        var msgCounts = await _persistence.GetRoomMessageCountsAsync();

        AllUsers = users.Select(u => new UserRow(
            u.Username,
            u.DisplayName,
            u.CreatedAt,
            u.IsAdmin,
            _chatStateService.IsUserOnline(u.Username)
        )).ToList();

        var systemRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "General", "Lobby" };
        AllRooms = _chatStateService.GetAllRoomNames()
            .Select(r => new RoomRow(
                r,
                _chatStateService.IsRoomPrivate(r),
                systemRooms.Contains(r),
                msgCounts.TryGetValue(r, out var c) ? c : 0,
                _chatStateService.GetOnlineUsers(r).Count
            ))
            .OrderBy(r => r.IsSystem ? 0 : 1)
            .ThenBy(r => r.Name)
            .ToList();
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync(string? msg)
    {
        var guard = await RequireAdmin();
        if (guard != null) return guard;

        // Keep admin marked as online while they're on this page
        _chatStateService.UpdateLastSeen(AdminUsername, DateTime.UtcNow);

        StatusMessage = msg;
        await PopulateViewModel();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteRoomAsync(string roomName)
    {
        var guard = await RequireAdmin();
        if (guard != null) return guard;

        if (!string.IsNullOrWhiteSpace(roomName))
        {
            var ok = await _chatStateService.DeleteRoomAsync(roomName, AdminUsername);
            if (ok)
            {
                // Kick anyone currently in the deleted room to General
                await _hubContext.Clients.Group(roomName).SendAsync("NavigateToRoom", "General");
                // Update room lists for all connected users (including admin's own chat/rooms tabs)
                await ChatHub.BroadcastRoomsToAllAsync(_hubContext, _chatStateService);
                // Force-push to admin's own connections in case they weren't caught above
                var adminConnIds = ChatHub.GetConnectionIds(AdminUsername);
                if (adminConnIds.Count > 0)
                {
                    var adminRooms = _chatStateService.GetRoomsForUser(AdminUsername)
                        .Select(r => new { name = r, isPrivate = _chatStateService.IsRoomPrivate(r) })
                        .ToList();
                    await _hubContext.Clients.Clients(adminConnIds).SendAsync("RoomsUpdated", adminRooms);
                }
            }
            var status = ok ? $"Room '{roomName}' deleted." : $"Could not delete '{roomName}'.";
            return RedirectToPage(new { msg = status });
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearMessagesAsync(string roomName)
    {
        var guard = await RequireAdmin();
        if (guard != null) return guard;

        if (!string.IsNullOrWhiteSpace(roomName))
        {
            var ok = await _persistence.ClearRoomMessagesAsync(roomName);
            if (ok)
            {
                // Also clear in-memory history so new joiners see empty room
                _chatStateService.ClearRoomHistory(roomName);
                // Push empty history to all clients currently viewing this room
                await _hubContext.Clients.Group(roomName).SendAsync("HistoryLoaded", new object[0]);
            }
            var status = ok ? $"Messages cleared in '{roomName}'." : $"Failed to clear '{roomName}'.";
            return RedirectToPage(new { msg = status });
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(string username)
    {
        var guard = await RequireAdmin();
        if (guard != null) return guard;

        if (!string.IsNullOrWhiteSpace(username))
        {
            var ok = await _persistence.DeleteUserAsync(username);
            var status = ok ? $"User '{username}' deleted." : $"Could not delete '{username}'.";
            return RedirectToPage(new { msg = status });
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostHeartbeatAsync()
    {
        var username = HttpContext.Session.GetString("Username");
        if (!string.IsNullOrWhiteSpace(username))
            _chatStateService.UpdateLastSeen(username, DateTime.UtcNow);
        return new OkResult();
    }

    public IActionResult OnPostLogout()
    {
        HttpContext.Session.Clear();
        return RedirectToPage("/Index");
    }
}
