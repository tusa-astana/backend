using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.FileProviders;
using TusaMap.Api.Data;
using TusaMap.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var frontendPath = builder.Configuration["Frontend:Path"];
if (!string.IsNullOrWhiteSpace(frontendPath) && Directory.Exists(frontendPath))
{
    builder.Environment.WebRootPath = Path.GetFullPath(frontendPath);
    builder.Environment.WebRootFileProvider = new PhysicalFileProvider(builder.Environment.WebRootPath);
}

var port = Environment.GetEnvironmentVariable("PORT") ?? "5001";
builder.WebHost.UseUrls($"http://*:{port}");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IMeetupVenueCatalog, CuratedAstanaVenueCatalog>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-event-submissions", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("meetup-writes", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("interest-actions", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("analytics", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});
builder.Services.AddSingleton<IInterestGroupCatalog, CuratedInterestGroupCatalog>();

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = "Data Source=tusamap.db";

builder.Services.AddDbContext<TusaMapDbContext>(options =>
{
    if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString);
});
builder.Services.AddScoped<IEventsStore, EventsStore>();
builder.Services.AddScoped<ITicketsStore, TicketsStore>();
builder.Services.AddScoped<IUserStore, UserStore>();
builder.Services.AddSingleton<ITelegramAuthService, TelegramAuthService>();
builder.Services.AddScoped<ITelegramBotService, TelegramBotService>();
builder.Services.AddHostedService<PrivateVenueNotifier>();
builder.Services.AddHostedService<PendingTelegramTicketCleanupService>();

var allowedCorsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
if (builder.Environment.IsDevelopment())
    allowedCorsOrigins = allowedCorsOrigins.Append("http://localhost:4200").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedCorsOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TusaMapDbContext>();
    db.Database.EnsureCreated();
    DatabaseSchema.EnsureCompatible(db);
    Tusa2026EventSeeder.Ensure(db, builder.Configuration);
}

if (builder.Configuration.GetValue("Telegram:ConfigureOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ITelegramBotService>().ConfigureWebAppAsync();
    await scope.ServiceProvider.GetRequiredService<ITelegramBotService>().ConfigureWebhookAsync();
}

if (app.Environment.IsDevelopment() && !builder.Configuration.GetValue("Telegram:HideSwagger", false))
    app.UseSwagger().UseSwaggerUI();

app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
