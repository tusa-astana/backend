using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private static readonly string[] AllowedEvents = ["catalog_view", "event_open", "event_share", "community_open"];
    private static readonly string[] AllowedCommunityRefs = ["rollmove-astana"];
    private readonly TusaMapDbContext _db;
    private readonly ITelegramAuthService _auth;
    private readonly IUserStore _users;

    public AnalyticsController(TusaMapDbContext db, ITelegramAuthService auth, IUserStore users)
    {
        _db = db;
        _auth = auth;
        _users = users;
    }

    [HttpPost]
    [EnableRateLimiting("analytics")]
    public IActionResult Track([FromBody] TrackEventRequest request, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        if (!AllowedEvents.Contains(request.Name, StringComparer.Ordinal)) return BadRequest("Unknown analytics event");
        if (request.Name == "catalog_view" && request.EventId is not null)
            return BadRequest("Catalog views cannot target an event");

        if (request.Name == "community_open" &&
            (request.EventId is not null || string.IsNullOrWhiteSpace(request.Ref) ||
             !AllowedCommunityRefs.Contains(request.Ref, StringComparer.Ordinal)))
            return BadRequest("Community opens require a known community reference");

        if (request.Name is "event_open" or "event_share")
        {
            if (string.IsNullOrWhiteSpace(request.EventId)) return BadRequest("EventId is required");
            var eventExists = _db.Events.AsNoTracking().Any(item =>
                item.Id == request.EventId && item.Status == "approved" && !item.IsDemo);
            if (!eventExists) return NotFound();
        }

        var user = _auth.ValidateInitData(initData);
        _db.FunnelEvents.Add(new Models.FunnelEvent { Name = request.Name, EventId = request.EventId, Ref = request.Ref, TelegramUserId = user?.Id });
        _db.SaveChanges();
        return NoContent();
    }

    [HttpPost("beacon")]
    [EnableRateLimiting("analytics")]
    public IActionResult TrackCommunityBeacon([FromForm] TrackCommunityBeaconRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Ref) ||
            !AllowedCommunityRefs.Contains(request.Ref, StringComparer.Ordinal))
            return BadRequest("Unknown community reference");

        _db.FunnelEvents.Add(new Models.FunnelEvent { Name = "community_open", Ref = request.Ref });
        _db.SaveChanges();
        return NoContent();
    }

    [HttpGet("summary")]
    public IActionResult Summary([FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        var summary = _db.FunnelEvents.AsNoTracking().GroupBy(x => x.Name).Select(group => new { name = group.Key, count = group.Count() }).ToList();
        return Ok(summary);
    }
}

public class TrackEventRequest
{
    [Required, StringLength(40)] public string Name { get; set; } = "";
    [StringLength(80)] public string? EventId { get; set; }
    [StringLength(120)] public string? Ref { get; set; }
}

public class TrackCommunityBeaconRequest
{
    [Required, StringLength(120)] public string Ref { get; set; } = "";
}