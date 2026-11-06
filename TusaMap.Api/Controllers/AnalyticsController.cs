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
    private static readonly string[] AllowedEvents = [
        "catalog_view", "event_open", "event_share", "community_open", "meetup_share_intent", "meetup_invite_open",
        "community_gathering_share", "community_gathering_open", "community_gathering_rsvp",
    ];
    private static readonly string[] AllowedCommunityRefs = ["rollmove-astana"];
    private static readonly string[] AllowedMeetupCampaigns = ["host-a", "host-b", "host-c"];
    private readonly TusaMapDbContext _db;
    private readonly ITelegramAuthService _auth;
    private readonly IUserStore _users;
    private readonly IMeetupVenueCatalog _venues;

    public AnalyticsController(TusaMapDbContext db, ITelegramAuthService auth, IUserStore users, IMeetupVenueCatalog venues)
    {
        _db = db;
        _auth = auth;
        _users = users;
        _venues = venues;
    }

    [HttpPost]
    [EnableRateLimiting("analytics")]
    public IActionResult Track([FromBody] TrackEventRequest request, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        if (!AllowedEvents.Contains(request.Name, StringComparer.Ordinal)) return BadRequest("Unknown analytics event");
        if (request.Name == "community_gathering_rsvp")
            return BadRequest("Community gathering RSVPs are recorded server-side");
        if (request.Name == "catalog_view" && request.EventId is not null)
            return BadRequest("Catalog views cannot target an event");

        if (request.Name == "community_open" &&
            (request.EventId is not null || string.IsNullOrWhiteSpace(request.Ref) ||
             !AllowedCommunityRefs.Contains(request.Ref, StringComparer.Ordinal)))
            return BadRequest("Community opens require a known community reference");

        if (request.Name is "meetup_share_intent" or "meetup_invite_open" &&
            (request.EventId is not null || string.IsNullOrWhiteSpace(request.Ref) ||
             !TryParseMeetupRef(request.Ref, out _, out _)))
            return BadRequest("Meetup analytics require a known venue and optional allowlisted campaign reference");

        var isCommunityGatheringEvent = request.Name is "community_gathering_share" or "community_gathering_open";
        if (isCommunityGatheringEvent &&
            (request.EventId is not null || string.IsNullOrWhiteSpace(request.Ref) || !IsActiveGatheringRef(request.Ref)))
            return BadRequest("Community gathering analytics require an active gathering reference");

        if (request.Name is "event_open" or "event_share")
        {
            if (string.IsNullOrWhiteSpace(request.EventId)) return BadRequest("EventId is required");
            var eventExists = _db.Events.AsNoTracking().Any(item =>
                item.Id == request.EventId && item.Status == "approved" && !item.IsDemo);
            if (!eventExists) return NotFound();
        }

        var user = _auth.ValidateInitData(initData);
        _db.FunnelEvents.Add(new Models.FunnelEvent
        {
            Name = request.Name,
            EventId = request.EventId,
            Ref = request.Ref,
            TelegramUserId = isCommunityGatheringEvent ? null : user?.Id,
        });
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

    [HttpGet("summary/meetup-density")]
    public async Task<IActionResult> MeetupDensitySummary(
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return StatusCode(StatusCodes.Status403Forbidden);

        var fromDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var throughDate = fromDate.AddDays(6);
        var fromDateText = fromDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var throughDateText = throughDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var qualifyingPlans = await _db.MeetupAttendances.AsNoTracking()
            .Where(item => item.MeetupDate.CompareTo(fromDateText) >= 0 && item.MeetupDate.CompareTo(throughDateText) <= 0)
            .GroupBy(item => new { item.VenueId, item.MeetupDate })
            .Where(group => group.Count() >= 2)
            .Select(group => new { group.Key.VenueId, group.Key.MeetupDate, RsvpCount = group.Count() })
            .OrderBy(item => item.MeetupDate)
            .ThenBy(item => item.VenueId)
            .ToListAsync(cancellationToken);

        var venueNames = _venues.GetVenues().ToDictionary(venue => venue.Id, venue => venue.Name, StringComparer.Ordinal);
        var plans = qualifyingPlans
            .Where(plan => venueNames.ContainsKey(plan.VenueId))
            .Select(plan => new MeetupDensityPlanResponse(plan.VenueId, venueNames[plan.VenueId], plan.MeetupDate, plan.RsvpCount))
            .ToArray();

        return Ok(new MeetupDensitySummaryResponse(fromDateText, throughDateText, plans));
    }

    [HttpGet("summary/meetup-campaigns")]
    public IActionResult MeetupCampaignSummary([FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return StatusCode(StatusCodes.Status403Forbidden);

        var events = _db.FunnelEvents.AsNoTracking()
            .Where(item => (item.Name == "meetup_share_intent" || item.Name == "meetup_invite_open") &&
                           item.Ref != null && item.Ref.Contains("|c:"))
            .Select(item => new { item.Name, item.Ref })
            .ToList();

        var summary = events
            .Select(item => TryParseMeetupRef(item.Ref, out var venueId, out var campaign)
                ? new { item.Name, VenueId = venueId, Campaign = campaign }
                : null)
            .Where(item => item is not null)
            .GroupBy(item => new { item!.Name, item.VenueId, item.Campaign })
            .Select(group => new { name = group.Key.Name, venueId = group.Key.VenueId, campaign = group.Key.Campaign, count = group.Count() })
            .OrderBy(item => item.campaign)
            .ThenBy(item => item.venueId)
            .ThenBy(item => item.name)
            .ToList();

        return Ok(summary);
    }

    private bool TryParseMeetupRef(string? value, out string venueId, out string? campaign)
    {
        venueId = "";
        campaign = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var campaignMarker = value.IndexOf("|c:", StringComparison.Ordinal);
        if (campaignMarker < 0)
        {
            venueId = value;
        }
        else
        {
            if (value.IndexOf("|c:", campaignMarker + 3, StringComparison.Ordinal) >= 0) return false;
            venueId = value[..campaignMarker];
            campaign = value[(campaignMarker + 3)..];
            if (!AllowedMeetupCampaigns.Contains(campaign, StringComparer.Ordinal)) return false;
        }

        var parsedVenueId = venueId;
        return _venues.GetVenues().Any(venue => string.Equals(venue.Id, parsedVenueId, StringComparison.Ordinal));
    }

    private bool IsActiveGatheringRef(string value)
    {
        const string prefix = "gathering:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var gatheringId = value[prefix.Length..];
        if (gatheringId.Length != 32 || !gatheringId.All(Uri.IsHexDigit)) return false;
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return _db.InterestGatherings.AsNoTracking().Any(item =>
            item.Id == gatheringId && !item.IsCancelled && item.MeetupDate.CompareTo(today) >= 0);
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

public sealed record MeetupDensitySummaryResponse(string FromDate, string ThroughDate, IReadOnlyList<MeetupDensityPlanResponse> Plans);

public sealed record MeetupDensityPlanResponse(string VenueId, string Venue, string Date, int RsvpCount);