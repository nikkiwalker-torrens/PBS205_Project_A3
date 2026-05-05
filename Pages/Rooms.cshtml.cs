using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using ChatClient.Web.Models;
using ChatClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ChatClient.Web.Pages;

public class RoomsModel : PageModel
{
    private readonly IChatStateService _chatStateService;
    private readonly ChatClient.Web.Services.ChatPersistenceService _persistence;

    public RoomsModel(IChatStateService chatStateService, ChatClient.Web.Services.ChatPersistenceService persistence)
    {
        _chatStateService = chatStateService;
        _persistence = persistence;
    }

    public string Username    { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<ChatRoomItem> Rooms { get; set; } = new();
    public string CurrentRoom { get; set; } = "Lobby";
    public int UsersOnline { get; set; }
    public string RabbitMqHostPort { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, int> UnreadCounts { get; set; } = new Dictionary<string, int>();
    public bool IsAdmin { get; set; }

    [BindProperty] public string NewRoomName { get; set; } = string.Empty;
    [BindProperty] public bool NewRoomIsPrivate { get; set; }

    // ── Guards ───────────────────────────────────────────────────────────────

    private IActionResult? RequireLogin()
    {
        if (string.IsNullOrWhiteSpace(HttpContext.Session.GetString("Username")))
            return RedirectToPage("/Index");
        return null;
    }

    // ── ViewModel ────────────────────────────────────────────────────────────

    private async Task PopulateViewModel()
    {
        Username    = HttpContext.Session.GetString("Username")    ?? "Guest";
        DisplayName = HttpContext.Session.GetString("DisplayName") ?? Username;
        CurrentRoom = HttpContext.Session.GetString("CurrentRoom") ?? "Lobby";

        // Resolve IsAdmin first so room list logic can use it
        IsAdmin = await _persistence.IsAdminAsync(Username);

        // Admins see ALL rooms (including private ones they aren't members of).
        // Regular users only see rooms they can access.
        var roomNames = IsAdmin
            ? _chatStateService.GetAllRoomNames()
            : _chatStateService.GetRoomsForUser(Username);

        Rooms = roomNames
            .Select(roomName => new ChatRoomItem
            {
                Name      = roomName,
                IsPrivate = _chatStateService.IsRoomPrivate(roomName)
            })
            .OrderBy(r => string.Equals(r.Name, "General", StringComparison.OrdinalIgnoreCase) ? 0
                        : string.Equals(r.Name, "Lobby",   StringComparison.OrdinalIgnoreCase) ? 1
                        : 2)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Total unique online users across all rooms
        UsersOnline = _chatStateService
            .GetAllRoomNames()
            .SelectMany(r => _chatStateService.GetOnlineUsers(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var connInfo = _chatStateService.GetConnectionInfo();
        RabbitMqHostPort = $"{connInfo.IpAddress}:{connInfo.Port}";
        UnreadCounts = _chatStateService.GetUnreadCounts(Username);
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync()
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        await PopulateViewModel();
        HttpContext.Session.SetString("CurrentRoom", CurrentRoom);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateRoomAsync()
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        await PopulateViewModel();

        if (!string.IsNullOrWhiteSpace(NewRoomName))
        {
            var roomName = NewRoomName.Trim();
            await _chatStateService.CreateRoomAsync(roomName, NewRoomIsPrivate, Username);

            HttpContext.Session.SetString("CurrentRoom", roomName);
            return RedirectToPage("/Chat", new { room = roomName });
        }

        return Page();
    }

    public async Task<IActionResult> OnPostJoinRoomAsync(string roomName)
    {
        var redirect = RequireLogin();
        if (redirect != null) return redirect;

        if (!string.IsNullOrWhiteSpace(roomName))
        {
            HttpContext.Session.SetString("CurrentRoom", roomName);
            return RedirectToPage("/Chat", new { room = roomName });
        }

        await PopulateViewModel();
        return Page();
    }

    /// <summary>Signs the user out.</summary>
    public IActionResult OnPostLogout()
    {
        HttpContext.Session.Clear();
        return RedirectToPage("/Index");
    }
}
