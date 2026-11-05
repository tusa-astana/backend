using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/organizer")]
public class OrganizerController : ControllerBase
{
    private readonly TusaMapDbContext _db;
    private readonly ITelegramAuthService _auth;
    private readonly IUserStore _users;
    private readonly IConfiguration _configuration;

    public OrganizerController(TusaMapDbContext db, ITelegramAuthService auth, IUserStore users, IConfiguration configuration)
    {
        _db = db;
        _auth = auth;
        _users = users;
        _configuration = configuration;
    }

    [HttpGet("subscription/offer")]
    public IActionResult SubscriptionOffer()
    {
        var stars = _configuration.GetValue<int>("Payments:TelegramSubscriptionStars");
        var enabled = _configuration.GetValue("Payments:OrganizerProEnabled", false);
        return Ok(new
        {
            available = enabled && stars > 0,
            stars = Math.Max(0, stars),
            durationDays = 30,
            features = new[] { "Цифровые инструменты организатора внутри TUSA" }
        });
    }

    [HttpGet("engagement")]
    public async Task<ActionResult<IReadOnlyList<OrganizerEngagementRow>>> Engagement(
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null) return Unauthorized();

        var isAdmin = _users.IsAdmin(user.Id);
        var eventsQuery = _db.Events.AsNoTracking()
            .Where(item => item.Status == "approved" && !item.IsDemo);
        if (!isAdmin)
            eventsQuery = eventsQuery.Where(item => item.OrganizerTelegramId == user.Id);

        var events = await eventsQuery
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Title)
            .Select(item => new { item.Id, item.Title, item.Date })
            .ToListAsync(cancellationToken);
        if (events.Count == 0) return Ok(Array.Empty<OrganizerEngagementRow>());

        var eventIds = events.Select(item => item.Id).ToArray();
        var views = await _db.FunnelEvents.AsNoTracking()
            .Where(item => item.Name == "event_open" && item.EventId != null && eventIds.Contains(item.EventId))
            .GroupBy(item => item.EventId!)
            .Select(group => new { EventId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.EventId, item => item.Count, cancellationToken);
        var shares = await _db.FunnelEvents.AsNoTracking()
            .Where(item => item.Name == "event_share" && item.EventId != null && eventIds.Contains(item.EventId))
            .GroupBy(item => item.EventId!)
            .Select(group => new { EventId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.EventId, item => item.Count, cancellationToken);
        var rsvps = await _db.EventParticipations.AsNoTracking()
            .Where(item => eventIds.Contains(item.EventId))
            .GroupBy(item => item.EventId)
            .Select(group => new { EventId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.EventId, item => item.Count, cancellationToken);

        return Ok(events.Select(item => new OrganizerEngagementRow(
            item.Id,
            item.Title,
            item.Date,
            views.GetValueOrDefault(item.Id),
            shares.GetValueOrDefault(item.Id),
            rsvps.GetValueOrDefault(item.Id))).ToArray());
    }

    [HttpGet("subscription")]
    public IActionResult Subscription([FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null) return Unauthorized();
        _users.Upsert(user);
        var subscription = _db.OrganizerSubscriptions.AsNoTracking().FirstOrDefault(x => x.TelegramUserId == user.Id);
        var isActive = subscription?.Status == "active" && subscription.ExpiresAt > DateTime.UtcNow;
        return Ok(new
        {
            telegramUserId = user.Id,
            plan = isActive ? subscription!.Plan : "starter",
            status = isActive ? "active" : subscription?.Status == "refunded" ? "refunded" : "inactive",
            expiresAt = subscription?.ExpiresAt,
        });
    }
}

public sealed record OrganizerEngagementRow(string EventId, string EventTitle, string EventDate, long Views, long Shares, long Rsvps);
