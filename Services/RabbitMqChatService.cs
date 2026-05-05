using System.Text;
using System.Text.Json;
using ChatClient.Web.Hubs;
using ChatClient.Web.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ChatClient.Web.Services;

/// <summary>
/// Manages the RabbitMQ connection for publishing and consuming chat/presence messages.
/// Implements IHostedService so the connection is established on application startup
/// rather than lazily on the first message � this ensures the RabbitMQ status indicator
/// is accurate from the moment a user enters the lobby.
/// </summary>
public class RabbitMqChatService : IRabbitMqChatService, IHostedService, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<RabbitMqChatService> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private IConnection? _connection;
    private IChannel? _publishChannel;
    private IChannel? _consumeChannel;
    private string? _queueName;
    private volatile bool _initialized;
    private volatile bool _isConnected;

    public RabbitMqChatService(
        IOptions<RabbitMqOptions> options,
        IServiceProvider serviceProvider,
        IHubContext<ChatHub> hubContext,
        ILogger<RabbitMqChatService> logger)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    public bool IsConnected => _isConnected &&
                               _connection is { IsOpen: true } &&
                               _publishChannel is { IsOpen: true } &&
                               _consumeChannel is { IsOpen: true };

    // -------------------------------------------------------------------------
    // IHostedService � connect eagerly on app startup
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by the ASP.NET Core host when the application starts.
    /// Establishes the RabbitMQ connection immediately so the status indicator
    /// shows "Connected" as soon as the first user reaches the lobby.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync();
            _logger.LogInformation("RabbitMQ connection established at startup.");
        }
        catch (Exception ex)
        {
            // Log but do not throw � a RabbitMQ outage should not prevent the
            // web application from starting.  The service will attempt to
            // reconnect on the first publish if needed.
            _logger.LogWarning(ex, "RabbitMQ could not connect at startup; will retry on first use.");
        }
    }

    /// <summary>
    /// Called by the ASP.NET Core host when the application is shutting down.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeInternalAsync();
    }

    // -------------------------------------------------------------------------
    // Publishing
    // -------------------------------------------------------------------------

    public async Task PublishMessageAsync(ChatMessage message)
    {
        if (message == null)
            return;

        await EnsureInitializedAsync();

        if (_publishChannel == null)
            throw new InvalidOperationException("RabbitMQ publish channel is not initialized.");

        message.Room = string.IsNullOrWhiteSpace(message.Room) ? "General" : message.Room.Trim();
        message.Username = message.Username?.Trim() ?? string.Empty;
        message.Message = message.Message?.Trim() ?? string.Empty;
        message.TimestampUtc = message.TimestampUtc == default ? DateTime.UtcNow : message.TimestampUtc;

        var json = JsonSerializer.Serialize(message);
        var body = Encoding.UTF8.GetBytes(json);

        try
        {
            await _publishChannel.BasicPublishAsync(
                exchange: _options.ExchangeName,
                routingKey: string.Empty,
                mandatory: false,
                body: body);

            _isConnected = true;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.LogError(ex, "Failed publishing chat message to RabbitMQ.");
            throw;
        }
    }

    public async Task PublishPresenceAsync(PresenceMessage message)
    {
        if (message == null)
            return;

        await EnsureInitializedAsync();

        if (_publishChannel == null)
            throw new InvalidOperationException("RabbitMQ publish channel is not initialized.");

        message.Room = string.IsNullOrWhiteSpace(message.Room) ? "General" : message.Room.Trim();
        message.Username = message.Username?.Trim() ?? string.Empty;
        message.Action = string.IsNullOrWhiteSpace(message.Action) ? "heartbeat" : message.Action.Trim();
        message.TimestampUtc = message.TimestampUtc == default ? DateTime.UtcNow : message.TimestampUtc;

        var json = JsonSerializer.Serialize(message);
        var body = Encoding.UTF8.GetBytes(json);

        try
        {
            await _publishChannel.BasicPublishAsync(
                exchange: _options.ExchangeName,
                routingKey: string.Empty,
                mandatory: false,
                body: body);

            _isConnected = true;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.LogError(ex, "Failed publishing presence message to RabbitMQ.");
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Internal � init / dispose
    // -------------------------------------------------------------------------

    private async Task EnsureInitializedAsync()
    {
        if (_initialized && IsConnected)
            return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized && IsConnected)
                return;

            await DisposeInternalAsync();

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            _logger.LogInformation(
                "Connecting to RabbitMQ at {Host}:{Port}, exchange {Exchange}",
                _options.HostName,
                _options.Port,
                _options.ExchangeName);

            _connection = await factory.CreateConnectionAsync();
            _publishChannel = await _connection.CreateChannelAsync();
            _consumeChannel = await _connection.CreateChannelAsync();

            _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            _connection.ConnectionBlockedAsync += OnConnectionBlockedAsync;
            _connection.ConnectionUnblockedAsync += OnConnectionUnblockedAsync;

            await _publishChannel.ExchangeDeclareAsync(
                exchange: _options.ExchangeName,
                type: "topic",
                durable: true,
                autoDelete: false);
            await _consumeChannel.ExchangeDeclareAsync(
                exchange: _options.ExchangeName,
                type: "topic",
                durable: true,
                autoDelete: false);

            _queueName = $"chatclient.web.{Environment.MachineName}.{Guid.NewGuid():N}";

            await _consumeChannel.QueueDeclareAsync(
                queue: _queueName,
                durable: false,
                exclusive: true,
                autoDelete: true);

            await _consumeChannel.QueueBindAsync(
                queue: _queueName,
                exchange: _options.ExchangeName,
                routingKey: string.Empty);

            var consumer = new AsyncEventingBasicConsumer(_consumeChannel);

            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    await HandleDeliveryAsync(ea);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling RabbitMQ delivery.");
                }
            };

            await _consumeChannel.BasicConsumeAsync(
                queue: _queueName,
                autoAck: true,
                consumer: consumer);

            _initialized = true;
            _isConnected = true;

            _logger.LogInformation(
                "RabbitMQ initialized successfully. Queue: {QueueName}",
                _queueName);
        }
        catch (Exception ex)
        {
            _initialized = false;
            _isConnected = false;
            _logger.LogError(ex, "RabbitMQ initialization failed.");
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        _isConnected = false;
        _logger.LogWarning("RabbitMQ connection shut down: {ReplyText}", args.ReplyText);
        return Task.CompletedTask;
    }

    private Task OnConnectionBlockedAsync(object sender, ConnectionBlockedEventArgs args)
    {
        _isConnected = false;
        _logger.LogWarning("RabbitMQ connection blocked: {Reason}", args.Reason);
        return Task.CompletedTask;
    }

    private Task OnConnectionUnblockedAsync(object sender, AsyncEventArgs args)
    {
        _isConnected = true;
        _logger.LogInformation("RabbitMQ connection unblocked.");
        return Task.CompletedTask;
    }

    private async Task HandleDeliveryAsync(BasicDeliverEventArgs ea)
    {
        var json = Encoding.UTF8.GetString(ea.Body.ToArray());

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Action", out _))
            {
                var presence = JsonSerializer.Deserialize<PresenceMessage>(json, _jsonOptions);

                if (presence != null)
                {
                    presence.Room = string.IsNullOrWhiteSpace(presence.Room) ? "General" : presence.Room.Trim();
                    presence.TimestampUtc = presence.TimestampUtc == default ? DateTime.UtcNow : presence.TimestampUtc;

                    var chatState = _serviceProvider.GetService(typeof(IChatStateService)) as ChatStateService;
                    if (chatState != null && !string.IsNullOrWhiteSpace(presence.Username))
                    {
                        // Stamp the user as online on THIS instance so IsUserOnline() works locally
                        if (string.Equals(presence.Action, "offline", StringComparison.OrdinalIgnoreCase))
                            chatState.UpdateLastSeen(presence.Username, DateTime.MinValue);
                        else
                            chatState.UpdateLastSeen(presence.Username, DateTime.UtcNow);

                        // Also ensure the user is registered as a room member on this instance
                        if (!string.Equals(presence.Action, "offline", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(presence.Username))
                        {
                            chatState.JoinRoom(presence.Username, presence.Room);
                        }

                        // Re-broadcast the full presence list to all local SignalR connections
                        // in this room so their member sidebar updates immediately
                        var members = chatState
                            .GetRoomMembers(presence.Room)
                            .Select(m => new
                            {
                                username = m,
                                displayName = ChatHub.GetDisplayName(m),
                                // Only mark online if user is currently in this room
                                isOnline = chatState.IsUserOnline(m) &&
                                              ChatHub.GetCurrentRoom(m).Equals(
                                                  presence.Room, StringComparison.OrdinalIgnoreCase)
                            })
                            .ToList();

                        await _hubContext.Clients.Group(presence.Room).SendAsync("PresenceUpdated", new
                        {
                            room = presence.Room,
                            onlineCount = members.Count(m => m.isOnline),
                            members
                        });
                    }

                    _isConnected = true;
                    return;
                }
            }

            var message = JsonSerializer.Deserialize<ChatMessage>(json, _jsonOptions);

            if (message != null && !string.IsNullOrWhiteSpace(message.Message))
            {
                message.Room = string.IsNullOrWhiteSpace(message.Room) ? "General" : message.Room.Trim();
                message.TimestampUtc = message.TimestampUtc == default ? DateTime.UtcNow : message.TimestampUtc;

                var chatState = _serviceProvider.GetService(typeof(IChatStateService)) as ChatStateService;
                if (chatState != null)
                {
                    var historyItem = new ChatHistoryItem
                    {
                        Room = message.Room,
                        Username = message.Username ?? string.Empty,
                        Message = message.Message ?? string.Empty,
                        TimestampUtc = message.TimestampUtc,
                        ItemType = "message",
                        StatusText = string.Empty,
                        ReplyToUsername = message.ReplyToUsername,
                        ReplyToText = message.ReplyToText
                    };

                    chatState.AddMessageToHistory(message.Room, historyItem);

                    // Persist here — this is the single write path for all messages
                    // regardless of which instance sent them.
                    var persistence = _serviceProvider.GetService(typeof(ChatPersistenceService)) as ChatPersistenceService;
                    if (persistence != null)
                        _ = persistence.SaveMessageAsync(historyItem);
                }

                await _hubContext.Clients.Group(message.Room).SendAsync("ReceiveMessage", message);
                _isConnected = true;

                // Push updated unread counts to every user who can see this room
                // except the sender — they sent the message so it is not unread for them.
                if (chatState != null)
                {
                    var senderUsername = message.Username ?? string.Empty;
                    var senderDisplayName = ChatHub.GetDisplayName(senderUsername);

                    foreach (var member in chatState.GetRoomMembers(message.Room))
                    {
                        // Skip the sender — messages they sent are never unread for them
                        if (string.Equals(member, senderUsername, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(member, senderDisplayName, StringComparison.OrdinalIgnoreCase)) continue;

                        var counts = chatState.GetUnreadCounts(member);
                        var connectionIds = ChatHub.GetConnectionIds(member);
                        if (connectionIds.Count > 0)
                            await _hubContext.Clients.Clients(connectionIds).SendAsync("UnreadCountsUpdated", counts);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize RabbitMQ payload.");
        }
    }

    private async Task DisposeInternalAsync()
    {
        _initialized = false;
        _isConnected = false;

        try
        {
            if (_connection != null)
            {
                _connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
                _connection.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
                _connection.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
            }
        }
        catch { }

        try
        {
            if (_publishChannel != null)
            {
                await _publishChannel.DisposeAsync();
                _publishChannel = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed disposing publish channel.");
        }

        try
        {
            if (_consumeChannel != null)
            {
                await _consumeChannel.DisposeAsync();
                _consumeChannel = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed disposing consume channel.");
        }

        try
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed disposing RabbitMQ connection.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeInternalAsync();
    }
}