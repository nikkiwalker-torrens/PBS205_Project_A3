using Microsoft.EntityFrameworkCore;
using System;

namespace ChatClient.Web.Data;

// ---------------------------------------------------------------------------
// Entity classes — kept simple and flat so they map cleanly to SQLite tables.
// ---------------------------------------------------------------------------

public class MessageEntity
{
    public int Id { get; set; }
    public string Room { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }

    // "message" or "status"
    public string ItemType { get; set; } = "message";

    // Only populated for status events
    public string StatusText { get; set; } = string.Empty;
    public string? ReplyToUsername { get; set; }
    public string? ReplyToText { get; set; }
}

public class RoomEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // True if this room has a private-invite list
    public bool IsPrivate { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Username of whoever created this room. Empty for system rooms (General, Lobby).</summary>
    public string CreatedBy { get; set; } = string.Empty;
}

public class PrivateRoomMemberEntity
{
    public int Id { get; set; }
    public string Room { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// User accounts (login / registration)
// ---------------------------------------------------------------------------

/// <summary>
/// Stores registered user credentials.  Passwords are stored as PBKDF2 hashes
/// — never in plaintext.
/// </summary>
public class UserEntity
{
    public int Id { get; set; }

    /// <summary>Case-insensitive unique login identifier (3–32 chars, alphanumeric + _ -).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>PBKDF2-SHA256 hash produced by PasswordHasher.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Friendly display name shown in chat (1–32 chars, any characters).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Grants access to the admin panel when true (stored as INTEGER 0/1 in PostgreSQL).</summary>
    public int IsAdmin { get; set; }
}

// ---------------------------------------------------------------------------
// DbContext
// ---------------------------------------------------------------------------

/// <summary>
/// Tracks the last time a user read each room so unread counts can be
/// recalculated from the message log after a server restart.
/// </summary>
public class UserLastReadEntity
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
    public DateTime LastReadUtc { get; set; }
}

public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options) { }

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<PrivateRoomMemberEntity> PrivateRoomMembers => Set<PrivateRoomMemberEntity>();
    public DbSet<UserLastReadEntity> UserLastReads => Set<UserLastReadEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessageEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Room);
            e.HasIndex(x => x.TimestampUtc);
            // Unique constraint prevents duplicate rows even under concurrent writes
            // from multiple instances receiving the same RabbitMQ delivery.
            e.HasIndex(x => new { x.Room, x.Username, x.TimestampUtc, x.Message })
             .IsUnique();
        });

        modelBuilder.Entity<RoomEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<PrivateRoomMemberEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Room, x.Username }).IsUnique();
        });

        modelBuilder.Entity<UserLastReadEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Username, x.Room }).IsUnique();
        });

        modelBuilder.Entity<UserEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
        });

        // Seed the Lobby so it always exists
        modelBuilder.Entity<RoomEntity>().HasData(new RoomEntity
        {
            Id = 1,
            Name = "Lobby",
            IsPrivate = false,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }
}