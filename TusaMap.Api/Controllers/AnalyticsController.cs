using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
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
        "search_term", "category_filter", "promotion_invoice_cancelled", "ticket_seller_click",
    ];
    private static readonly string[] AllowedCategories = ["концерт", "вечеринка", "развлечения"];
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

        if (request.Name == "search_term" &&
            (request.EventId is not null || !IsSafeSearchTerm(request.Ref)))
            return BadRequest("Search analytics accepts one normalized word only");

        if (request.Name == "category_filter" &&
            (request.EventId is not null || request.Ref is null || !AllowedCategories.Contains(request.Ref, StringComparer.Ordinal)))
            return BadRequest("Unknown category filter");

        if (request.Name == "promotion_invoice_cancelled")
        {
            var promotionUser = _auth.ValidateInitData(initData);
            if (promotionUser is null || string.IsNullOrWhiteSpace(request.EventId) ||
                !_db.EventPromotions.Any(item => item.EventId == request.EventId && item.OrganizerTelegramId == promotionUser.Id && item.Status == "pending"))
                return StatusCode(StatusCodes.Status403Forbidden);
            request.Ref = null;
        }

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

        if (request.Name == "ticket_seller_click")
        {
            if (string.IsNullOrWhiteSpace(request.EventId)) return BadRequest("EventId is required");
            var hasExternalSeller = _db.Events.AsNoTracking().Any(item =>
                item.Id == request.EventId && item.Status == "approved" && !item.IsDemo && item.TicketUrl != "");
            if (!hasExternalSeller) return NotFound();
        }

        if (request.Name == "search_term") request.Ref = request.Ref!.ToLowerInvariant();

        var user = _auth.ValidateInitData(initData);
        _db.FunnelEvents.Add(new Models.FunnelEvent
        {
            Name = request.Name,
            EventId = request.EventId,
            Ref = request.Ref,
            TelegramUserId = isCommunityGatheringEvent || request.Name is "search_term" or "category_filter" or "promotion_invoice_cancelled" or "ticket_seller_click" ? null : user?.Id,
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

    [HttpGet("summary/daily")]
    public async Task<IActionResult> DailySummary(
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return StatusCode(StatusCodes.Status403Forbidden);

        var todayAstana = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var fromDate = todayAstana.AddDays(-6);
        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-5);
        var throughUtc = fromUtc.AddDays(7);
        var events = await _db.FunnelEvents.AsNoTracking()
            .Where(item => item.CreatedAt >= fromUtc && item.CreatedAt < throughUtc)
            .Select(item => new { item.Name, item.CreatedAt })
            .ToListAsync(cancellationToken);

        var rows = events
            .GroupBy(item => new { Date = DateOnly.FromDateTime(item.CreatedAt.AddHours(5)), item.Name })
            .Select(group => new DailyFunnelMetricResponse(
                group.Key.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                group.Key.Name,
                group.Count()))
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Name)
            .ToArray();

        return Ok(new DailyFunnelSummaryResponse(
            fromDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            todayAstana.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            rows));
    }

    [HttpGet("summary/insights")]
    public async Task<IActionResult> InsightsSummary(
        [FromQuery] int days,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        if (days is not (7 or 30 or 90)) return BadRequest("Days must be 7, 30, or 90");

        var throughDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var fromDate = throughDate.AddDays(-(days - 1));
        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-5);
        var throughUtc = fromUtc.AddDays(days);
        var events = await _db.FunnelEvents.AsNoTracking()
            .Where(item => item.CreatedAt >= fromUtc && item.CreatedAt < throughUtc)
            .Select(item => new { item.Name, item.Ref, item.EventId, item.CreatedAt })
            .ToListAsync(cancellationToken);

        var daily = events
            .GroupBy(item => new { Date = DateOnly.FromDateTime(item.CreatedAt.AddHours(5)), item.Name })
            .Select(group => new DailyFunnelMetricResponse(
                group.Key.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), group.Key.Name, group.Count()))
            .OrderBy(item => item.Date).ThenBy(item => item.Name).ToArray();
        var topSearchTerms = events
            .Where(item => item.Name == "search_term" && IsSafeSearchTerm(item.Ref))
            .GroupBy(item => item.Ref!)
            .Select(group => new AnalyticsCountResponse(group.Key, group.Count()))
            .Where(item => item.Count >= 3)
            .OrderByDescending(item => item.Count).ThenBy(item => item.Name).Take(20).ToArray();

        var openedEventIds = events.Where(item => item.Name == "event_open" && item.EventId is not null)
            .Select(item => item.EventId!).Distinct().ToArray();
        var categoryByEvent = await _db.Events.AsNoTracking()
            .Where(item => openedEventIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Category })
            .ToDictionaryAsync(item => item.Id, item => item.Category, cancellationToken);
        var popularCategories = events
            .Where(item => item.Name == "event_open" && item.EventId is not null && categoryByEvent.ContainsKey(item.EventId))
            .GroupBy(item => categoryByEvent[item.EventId!])
            .Select(group => new AnalyticsCountResponse(group.Key, group.Count()))
            .OrderByDescending(item => item.Count).ThenBy(item => item.Name).ToArray();

        var promotionFunnel = new PromotionAnalyticsResponse(
            events.Count(item => item.Name == "promotion_offer_view"),
            events.Count(item => item.Name == "promotion_invoice_created"),
            events.Count(item => item.Name == "promotion_pre_checkout"),
            events.Count(item => item.Name == "promotion_invoice_cancelled"),
            events.Count(item => item.Name == "ticket_seller_click"),
            events.Count(item => item.Name == "promotion_paid"),
            events.Count(item => item.Name == "promotion_refunded"));

        return Ok(new AnalyticsInsightsSummaryResponse(
            fromDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            throughDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            daily, topSearchTerms, popularCategories, promotionFunnel));
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

    private static bool IsSafeSearchTerm(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length is >= 3 and <= 24 &&
           Regex.IsMatch(value, @"\A\p{L}{3,24}\z", RegexOptions.CultureInvariant);
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

public sealed record DailyFunnelSummaryResponse(string FromDate, string ThroughDate, IReadOnlyList<DailyFunnelMetricResponse> Rows);

public sealed record DailyFunnelMetricResponse(string Date, string Name, int Count);

public sealed record AnalyticsCountResponse(string Name, int Count);

public sealed record PromotionAnalyticsResponse(int OfferViews, int InvoiceStarts, int PreCheckout, int CancelledOrClosed, int TicketSellerClicks, int Paid, int Refunded);

public sealed record AnalyticsInsightsSummaryResponse(
    string FromDate,
    string ThroughDate,
    IReadOnlyList<DailyFunnelMetricResponse> Daily,
    IReadOnlyList<AnalyticsCountResponse> TopSearchTerms,
    IReadOnlyList<AnalyticsCountResponse> PopularCategories,
    PromotionAnalyticsResponse PromotionFunnel);