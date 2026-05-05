using ChatClient.Web.Data;
using ChatClient.Web.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace ChatClient.Web.Services;

/// <summary>
/// Wraps all PostgreSQL reads and writes. ChatStateService calls this on every
/// mutation so that state survives restarts and is shared across all instances
/// connecting to the same database.
/// </summary>
public class ChatPersistenceService
{
    private readonly IDbContextFactory<ChatDbContext> _dbFactory;
    private readonly ILogger<ChatPersistenceService> _logger;

    private const int MaxMessagesPerRoom = 500;

    public ChatPersistenceService(
        IDbContextFactory<ChatDbContext> dbFactory,
        ILogger<ChatPersistenceService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Schema bootstrap
    // -------------------------------------------------------------------------

    public async Task EnsureCreatedAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // EnsureCreatedAsync creates all EF-mapped tables on first run.
        // On an existing database it is a no-op — safe to call every startup.
        await db.Database.EnsureCreatedAsync();

        // Add DisplayName column to Users if it doesn't exist (upgrade migration)
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Users\" ADD COLUMN \"DisplayName\" TEXT NOT NULL DEFAULT ''");
        }
        catch { /* column already exists — safe to ignore */ }

        // Add CreatedBy column to Rooms if it doesn't exist (upgrade migration)
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Rooms\" ADD COLUMN \"CreatedBy\" TEXT NOT NULL DEFAULT ''");
        }
        catch { /* column already exists — safe to ignore */ }

        // Reply-to columns on Messages
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Messages\" ADD COLUMN \"ReplyToUsername\" TEXT NULL"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Messages\" ADD COLUMN \"ReplyToText\" TEXT NULL"); } catch { }

        // IsAdmin column on Users (upgrade migration)
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ADD COLUMN \"IsAdmin\" INTEGER NOT NULL DEFAULT 0"); } catch { }

        // Seed admin account on every startup
        await SeedAdminAccountAsync(db);
    }

    private async Task SeedAdminAccountAsync(ChatDbContext db)
    {
        // Use raw SQL exclusively — never touch db.Users here.
        // EF will try to SELECT the IsAdmin column even on AnyAsync, which fails
        // if we only just added it via ALTER TABLE in the same startup.
        var rows = await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"IsAdmin\" = 1 WHERE lower(\"Username\") = 'admin'");

        if (rows > 0) return; // admin already existed, flag applied

        // Insert fresh admin account entirely via raw SQL
        var hash = PasswordHasher.Hash("admin");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Users\" (\"Username\", \"PasswordHash\", \"DisplayName\", \"CreatedAtUtc\", \"IsAdmin\") " +
            "VALUES ('admin', {0}, 'Admin', NOW(), 1)",
            hash);
    }

    // -------------------------------------------------------------------------
    // User accounts
    // -------------------------------------------------------------------------

    /// <summary>Registers a new user. Returns false if username already exists.</summary>
    public async Task<bool> RegisterUserAsync(string username, string passwordHash, string displayName)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordHash))
            return false;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Raw SQL check avoids EF mapping the IsAdmin column before ALTER TABLE has run
            var existsRows = await db.Database
                .SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS \"Value\" FROM \"Users\" WHERE lower(\"Username\") = lower({0})",
                    username.Trim())
                .ToListAsync();
            if (existsRows.FirstOrDefault() > 0) return false;

            var dn = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Users\" (\"Username\", \"PasswordHash\", \"DisplayName\", \"CreatedAtUtc\", \"IsAdmin\") " +
                "VALUES ({0}, {1}, {2}, NOW(), 0)",
                username.Trim(), passwordHash, dn);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register user {Username}. Error: {Error}", username, ex.Message);
            return false;
        }
    }

    /// <summary>Returns the stored password hash, or null if the user does not exist.</summary>
    public async Task<string?> GetPasswordHashAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // Raw SQL avoids EF trying to map the IsAdmin column which may not
            // exist on databases that haven't been migrated yet this session.
            var rows = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT \"PasswordHash\" AS \"Value\" FROM \"Users\" WHERE lower(\"Username\") = lower({0})",
                    username)
                .ToListAsync();
            return rows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to look up user {Username}.", username);
            return null;
        }
    }

    /// <summary>Returns the canonical (original-case) stored username, or null if not found.</summary>
    public async Task<string?> GetCanonicalUsernameAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var rows = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT \"Username\" AS \"Value\" FROM \"Users\" WHERE lower(\"Username\") = lower({0})",
                    username)
                .ToListAsync();
            return rows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get canonical username for {Username}.", username);
            return null;
        }
    }

    /// <summary>Returns the display name for a username, or the username itself if not found.</summary>
    public async Task<string> GetDisplayNameAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return username;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var rows = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT \"DisplayName\" AS \"Value\" FROM \"Users\" WHERE lower(\"Username\") = lower({0})",
                    username)
                .ToListAsync();
            var displayName = rows.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(displayName) ? displayName : username;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get display name for {Username}.", username);
            return username;
        }
    }

    // -------------------------------------------------------------------------
    // Rooms
    // -------------------------------------------------------------------------

    public async Task<List<(string Name, bool IsPrivate)>> LoadAllRoomsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Rooms
            .OrderBy(r => r.Name == "Lobby" ? 0 : 1)
            .ThenBy(r => r.Name)
            .Select(r => new { r.Name, r.IsPrivate })
            .ToListAsync();
        return rows.Select(r => (r.Name, r.IsPrivate)).ToList();
    }

    public async Task SaveRoomAsync(string roomName, bool isPrivate, string createdBy = "")
    {
        if (string.IsNullOrWhiteSpace(roomName)) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var exists = await db.Rooms.AnyAsync(r => r.Name == roomName);
            if (!exists)
            {
                db.Rooms.Add(new RoomEntity
                {
                    Name = roomName,
                    IsPrivate = isPrivate,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = createdBy ?? string.Empty
                });
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist room {Room}.", roomName);
        }
    }

    public async Task<string> GetRoomCreatorAsync(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return string.Empty;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var room = await db.Rooms.FirstOrDefaultAsync(r => r.Name == roomName);
            return room?.CreatedBy ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get creator for room {Room}.", roomName);
            return string.Empty;
        }
    }

    public async Task<bool> DeleteRoomAsync(string roomName, string requestingUser)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return false;

        if (string.Equals(roomName, "General", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(roomName, "Lobby", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Always delete messages/members regardless of whether the room
            // row exists — the room may only exist in memory (not persisted).
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"Messages\" WHERE \"Room\" = {0}", roomName);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"PrivateRoomMembers\" WHERE \"Room\" = {0}", roomName);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"UserLastReads\" WHERE \"Room\" = {0}", roomName);

            // Remove the Rooms row if it exists (may not exist for orphaned rooms)
            var room = await db.Rooms.FirstOrDefaultAsync(r => r.Name == roomName);
            if (room != null)
            {
                db.Rooms.Remove(room);
                await db.SaveChangesAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete room {Room}.", roomName);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Private room members
    // -------------------------------------------------------------------------

    public async Task<List<(string Room, string Username)>> LoadAllPrivateMembersAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.PrivateRoomMembers
            .Select(m => new { m.Room, m.Username })
            .ToListAsync();
        return rows.Select(m => (m.Room, m.Username)).ToList();
    }

    public async Task SavePrivateMemberAsync(string room, string username)
    {
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(username)) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var exists = await db.PrivateRoomMembers
                .AnyAsync(m => m.Room == room && m.Username == username);

            if (!exists)
            {
                db.PrivateRoomMembers.Add(new PrivateRoomMemberEntity
                {
                    Room = room,
                    Username = username
                });
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist private member {User} in {Room}.", username, room);
        }
    }

    // -------------------------------------------------------------------------
    // Messages
    // -------------------------------------------------------------------------

    public async Task<List<ChatHistoryItem>> LoadRoomHistoryAsync(string room)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Messages
            .Where(m => m.Room == room)
            .OrderBy(m => m.TimestampUtc)
            .Select(m => new ChatHistoryItem
            {
                Room = m.Room,
                Username = m.Username,
                Message = m.Message,
                TimestampUtc = m.TimestampUtc,
                ItemType = m.ItemType,
                StatusText = m.StatusText,
                ReplyToUsername = m.ReplyToUsername,
                ReplyToText = m.ReplyToText
            })
            .ToListAsync();
    }

    public async Task SaveMessageAsync(ChatHistoryItem item)
    {
        if (item == null) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // ON CONFLICT DO NOTHING prevents duplicate rows when multiple instances
            // receive the same RabbitMQ delivery simultaneously.
            await db.Database.ExecuteSqlRawAsync(@"
                INSERT INTO ""Messages""
                    (""Room"", ""Username"", ""Message"", ""TimestampUtc"", ""ItemType"", ""StatusText"", ""ReplyToUsername"", ""ReplyToText"")
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
                ON CONFLICT DO NOTHING",
                item.Room ?? string.Empty,
                item.Username ?? string.Empty,
                item.Message ?? string.Empty,
                item.TimestampUtc == default ? DateTime.UtcNow : item.TimestampUtc,
                item.ItemType ?? "message",
                item.StatusText ?? string.Empty,
                (object?)item.ReplyToUsername ?? DBNull.Value,
                (object?)item.ReplyToText ?? DBNull.Value);

            await TrimRoomHistoryAsync(db, item.Room ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist message in {Room}.", item.Room);
        }
    }

    // -------------------------------------------------------------------------
    // User last-read timestamps
    // -------------------------------------------------------------------------

    public async Task SaveLastReadAsync(string username, string room, DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(room)) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(@"
                INSERT INTO ""UserLastReads"" (""Username"", ""Room"", ""LastReadUtc"")
                VALUES ({0}, {1}, {2})
                ON CONFLICT(""Username"", ""Room"") DO UPDATE
                SET ""LastReadUtc"" = EXCLUDED.""LastReadUtc""
                WHERE EXCLUDED.""LastReadUtc"" > ""UserLastReads"".""LastReadUtc""",
                username, room, timestampUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save last-read for {User} in {Room}.", username, room);
        }
    }

    public async Task<Dictionary<string, int>> LoadUnreadCountsForRoomAsync(string room)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(room)) return result;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var lastReads = await db.UserLastReads
                .Where(x => x.Room == room)
                .ToListAsync();

            foreach (var lr in lastReads)
            {
                // Get the display name for this user so we can exclude
                // their own messages (stored under displayName not username)
                var dnRows = await db.Database
                    .SqlQueryRaw<string>(
                        "SELECT \"DisplayName\" AS \"Value\" FROM \"Users\" WHERE lower(\"Username\") = lower({0})",
                        lr.Username)
                    .ToListAsync();
                var displayName = dnRows.FirstOrDefault() ?? lr.Username;

                var count = await db.Messages
                    .CountAsync(m => m.Room == room
                                  && m.ItemType == "message"
                                  && m.Username.ToLower() != lr.Username.ToLower()
                                  && m.Username.ToLower() != displayName.ToLower()
                                  && m.TimestampUtc > lr.LastReadUtc);
                if (count > 0)
                    result[lr.Username] = count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load unread counts for {Room}.", room);
        }
        return result;
    }

    /// <summary>Returns all distinct room names that appear in the Messages table.
    /// Used to recover rooms that exist in chat history but are missing from the Rooms table.</summary>
    public async Task<List<string>> GetRoomNamesFromMessagesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Messages
            .Select(m => m.Room)
            .Distinct()
            .ToListAsync();
    }

    // -------------------------------------------------------------------------
    // Admin helpers
    // -------------------------------------------------------------------------

    public async Task<bool> IsAdminAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = await db.Database
                .SqlQueryRaw<int>("SELECT \"IsAdmin\" AS \"Value\" FROM \"Users\" WHERE lower(\"Username\") = lower({0})", username)
                .ToListAsync();
            return result.FirstOrDefault() == 1;
        }
        catch { return false; }
    }

    public async Task<List<(string Username, string DisplayName, DateTime CreatedAt, bool IsAdmin)>> GetAllUsersAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var users = await db.Users
            .OrderBy(u => u.Username)
            .ToListAsync();
        return users.Select(u => (u.Username, u.DisplayName, u.CreatedAtUtc, u.IsAdmin == 1)).ToList();
    }

    public async Task<bool> DeleteUserAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        if (username.ToLower() == "admin") return false;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var rows = await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"Users\" WHERE lower(\"Username\") = lower({0}) AND lower(\"Username\") != 'admin'",
                username);
            return rows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete user {Username}.", username);
            return false;
        }
    }

    public async Task<bool> ClearRoomMessagesAsync(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return false;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"Messages\" WHERE \"Room\" = {0}", roomName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear messages for room {Room}.", roomName);
            return false;
        }
    }

    public async Task<Dictionary<string, int>> GetRoomMessageCountsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Messages
            .Where(m => m.ItemType == "message")
            .GroupBy(m => m.Room)
            .Select(g => new { Room = g.Key, Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Room, r => r.Count);
    }

    private async Task TrimRoomHistoryAsync(ChatDbContext db, string room)
    {
        var count = await db.Messages.CountAsync(m => m.Room == room);
        if (count <= MaxMessagesPerRoom) return;

        var excess = count - MaxMessagesPerRoom;
        var oldest = await db.Messages
            .Where(m => m.Room == room)
            .OrderBy(m => m.TimestampUtc)
            .Take(excess)
            .ToListAsync();

        db.Messages.RemoveRange(oldest);
        await db.SaveChangesAsync();
    }
}