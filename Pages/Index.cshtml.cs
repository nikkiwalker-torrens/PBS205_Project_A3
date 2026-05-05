using ChatClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.RegularExpressions;

namespace ChatClient.Web.Pages;

public class IndexModel : PageModel
{
    private readonly ChatPersistenceService _persistence;

    public IndexModel(ChatPersistenceService persistence)
    {
        _persistence = persistence;
    }

    public string? ErrorMessage { get; set; }
    public string  ActiveTab    { get; set; } = "login";

    // ---- login ----
    [BindProperty] public string LoginUsername { get; set; } = "";
    [BindProperty] public string LoginPassword { get; set; } = "";

    // ---- register ----
    [BindProperty] public string RegUsername    { get; set; } = "";
    [BindProperty] public string RegDisplayName { get; set; } = "";
    [BindProperty] public string RegPassword    { get; set; } = "";
    [BindProperty] public string RegConfirm     { get; set; } = "";

    // -------------------------------------------------------------------------

    public void OnGet()
    {
        if (!string.IsNullOrWhiteSpace(HttpContext.Session.GetString("Username")))
            Response.Redirect("/Rooms");
    }

    // ---- Login ----
    public async Task<IActionResult> OnPostLoginAsync()
    {
        ActiveTab = "login";

        var username = (LoginUsername ?? "").Trim();
        var password = LoginPassword ?? "";

        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorMessage = "Please enter your username.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "Please enter your password.";
            return Page();
        }

        // Case-insensitive login
        var storedHash = await _persistence.GetPasswordHashAsync(username);
        if (storedHash == null || !PasswordHasher.Verify(password, storedHash))
        {
            ErrorMessage = "Incorrect username or password.";
            return Page();
        }

        // Store canonical username and display name in session
        var canonical   = await _persistence.GetCanonicalUsernameAsync(username) ?? username;
        var displayName = await _persistence.GetDisplayNameAsync(username);
        // If no display name stored, use the canonical username as fallback
        if (string.IsNullOrWhiteSpace(displayName)) displayName = canonical;

        HttpContext.Session.SetString("Username",    canonical);
        HttpContext.Session.SetString("DisplayName", displayName);
        return RedirectToPage("/Rooms");
    }

    // ---- Register ----
    public async Task<IActionResult> OnPostRegisterAsync()
    {
        ActiveTab = "register";

        var username    = (RegUsername    ?? "").Trim();
        var displayName = (RegDisplayName ?? "").Trim();
        var password    = RegPassword ?? "";
        var confirm     = RegConfirm  ?? "";

        // Username rules
        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorMessage = "Username is required.";
            return Page();
        }
        if (username.Length < 3 || username.Length > 32)
        {
            ErrorMessage = "Username must be 3–32 characters.";
            return Page();
        }
        if (!Regex.IsMatch(username, @"^[A-Za-z0-9_\-]+$"))
        {
            ErrorMessage = "Username may only contain letters, numbers, _ and -.";
            return Page();
        }

        // Display name rules
        if (string.IsNullOrWhiteSpace(displayName))
        {
            ErrorMessage = "Display name is required.";
            return Page();
        }
        if (displayName.Length < 1 || displayName.Length > 32)
        {
            ErrorMessage = "Display name must be 1–32 characters.";
            return Page();
        }

        // Password rules
        if (password.Length < 8)
        {
            ErrorMessage = "Password must be at least 8 characters.";
            return Page();
        }
        if (!Regex.IsMatch(password, @"[A-Za-z]") || !Regex.IsMatch(password, @"[0-9]"))
        {
            ErrorMessage = "Password must contain at least one letter and one number.";
            return Page();
        }
        if (password != confirm)
        {
            ErrorMessage = "Passwords do not match.";
            return Page();
        }

        var hash = PasswordHasher.Hash(password);
        var ok   = await _persistence.RegisterUserAsync(username, hash, displayName);

        if (!ok)
        {
            ErrorMessage = $"The username '{username}' is already taken. Please choose a different username.";
            return Page();
        }

        HttpContext.Session.SetString("Username",    username);
        HttpContext.Session.SetString("DisplayName", displayName);
        return RedirectToPage("/Rooms");
    }
}
