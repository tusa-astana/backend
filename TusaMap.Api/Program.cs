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
var startupReady = 0;

if (app.Environment.IsDevelopment() && !builder.Configuration.GetValue("Telegram:HideSwagger", false))
    app.UseSwagger().UseSwaggerUI();

app.UseRouting();
app.Use(async (context, next) =>
{
    var isApiRequest = context.Request.Path.StartsWithSegments("/api");
    var isHealthCheck = context.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase);
    if (isApiRequest && !isHealthCheck && !HttpMethods.IsOptions(context.Request.Method) && Volatile.Read(ref startupReady) == 0)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { status = "starting" });
        return;
    }

    await next();
});
app.UseCors();
app.UseRateLimiter();
app.MapGet("/api/health", async (TusaMapDbContext db, CancellationToken cancellationToken) =>
{
    if (Volatile.Read(ref startupReady) == 0)
        return Results.Json(new { status = "starting" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    try
    {
        var connected = await db.Database.CanConnectAsync(cancellationToken);
        var database = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? "PostgreSQL"
            : "SQLite";
        return connected
            ? Results.Ok(new { status = "ok", database })
            : Results.Json(new { status = "unavailable", database }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch
    {
        return Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

await app.StartAsync();

try
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TusaMapDbContext>();
        db.Database.EnsureCreated();
        DatabaseSchema.EnsureCompatible(db);
        Tusa2026EventSeeder.Ensure(db, builder.Configuration);
    }

    Volatile.Write(ref startupReady, 1);
}
catch (Exception exception)
{
    app.Logger.LogCritical(exception, "Database initialization failed during startup");
    throw;
}

if (builder.Configuration.GetValue("Telegram:ConfigureOnStartup", true))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        using var scope = app.Services.CreateScope();
        var telegramBot = scope.ServiceProvider.GetRequiredService<ITelegramBotService>();
        await telegramBot.ConfigureWebAppAsync(timeout.Token);
        await telegramBot.ConfigureWebhookAsync(timeout.Token);
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        app.Logger.LogWarning("Telegram startup configuration timed out; API remains available");
    }
    catch (HttpRequestException exception)
    {
        app.Logger.LogWarning(exception, "Telegram startup configuration failed; API remains available");
    }
}

await app.WaitForShutdownAsync();
