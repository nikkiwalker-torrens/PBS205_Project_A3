using ChatClient.Web.Data;
using ChatClient.Web.Hubs;
using ChatClient.Web.Models;
using ChatClient.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// -------------------------------------------------------------------------
// Configuration
// -------------------------------------------------------------------------

builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection("RabbitMQ"));

// -------------------------------------------------------------------------
// PostgreSQL — shared across all instances on all networks.
// Connection string from appsettings.json or environment variable.
// -------------------------------------------------------------------------

var connectionString = builder.Configuration.GetConnectionString("ChatDb")
    ?? throw new InvalidOperationException(
        "PostgreSQL connection string 'ChatDb' is missing. " +
        "Add it to appsettings.json or as ConnectionStrings__ChatDb environment variable.");

builder.Services.AddDbContextFactory<ChatDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<ChatPersistenceService>();

// -------------------------------------------------------------------------
// App services
// -------------------------------------------------------------------------

builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IChatStateService, ChatStateService>();

builder.Services.AddSingleton<RabbitMqChatService>();
builder.Services.AddSingleton<IRabbitMqChatService>(sp => sp.GetRequiredService<RabbitMqChatService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitMqChatService>());

var app = builder.Build();

// -------------------------------------------------------------------------
// Bootstrap DB
// -------------------------------------------------------------------------

var persistence = app.Services.GetRequiredService<ChatPersistenceService>();
await persistence.EnsureCreatedAsync();

var chatState = app.Services.GetRequiredService<IChatStateService>() as ChatStateService;
if (chatState != null)
    await chatState.LoadFromDatabaseAsync();

// Wire ChatStateService → RabbitMqChatService so state mutations can publish.
var rabbitService = app.Services.GetRequiredService<IRabbitMqChatService>();
chatState?.SetRabbitMqService(rabbitService);

// -------------------------------------------------------------------------
// Pipeline
// -------------------------------------------------------------------------

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseSession();

app.MapRazorPages();
app.MapHub<ChatHub>("/chatHub");

app.Run();
