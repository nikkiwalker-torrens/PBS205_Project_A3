using ChatClient.Web.Models;

namespace ChatClient.Web.Services;

public interface IRabbitMqChatService
{
    Task PublishMessageAsync(ChatMessage message);
    Task PublishPresenceAsync(PresenceMessage message);
}