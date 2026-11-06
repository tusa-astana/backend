using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/meetups")]
public sealed class MeetupsController(
    TusaMapDbContext db,
    ITelegramAuthService telegramAuth,
    IUserStore users,
    IMeetupVenueCatalog venueCatalog) : ControllerBase
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd"];

    [HttpGet("venues")]
    public ActionResult<IReadOnlyList<MeetupVenueOptionResponse>> GetVenueOptions()
        => Ok(venueCatalog.GetVenues()
            .Select(venue => new MeetupVenueOptionResponse(venue.Id, venue.Name))
            .ToArray());

    [HttpGet]
    public async Task<ActionResult<MeetupCatalogResponse>> Get([FromQuery] string? date, CancellationToken cancellationToken)
    {
        var meetupDate = ParseDate(date) ?? AstanaToday();
        if (meetupDate < AstanaToday().AddDays(-1) || meetupDate > AstanaToday().AddDays(60))
            return BadRequest("Дата должна быть в диапазоне от вчера до 60 дней вперёд.");

        var venues = venueCatalog.GetVenues();
        var initDataUser = telegramAuth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        var dateText = meetupDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var attendances = await db.MeetupAttendances.AsNoTracking()
            .Where(item => item.MeetupDate == dateText)
            .ToListAsync(cancellationToken);
        var visibleUserIds = attendances.Where(item => !item.IsAnonymous).Select(item => item.TelegramUserId).Distinct().ToArray();
        var publicUsers = await db.Users.AsNoTracking()
            .Where(user => visibleUserIds.Contains(user.TelegramUserId))
            .ToDictionaryAsync(user => user.TelegramUserId, cancellationToken);

        var plans = venues.Select(venue =>
        {
            var rows = attendances.Where(item => item.VenueId == venue.Id).ToArray();
            var attendees = rows.Select((attendance, index) =>
            {
                publicUsers.TryGetValue(attendance.TelegramUserId, out var user);
                var isYou = initDataUser is not null && attendance.TelegramUserId == initDataUser.Id;
                var displayName = attendance.IsAnonymous
                    ? "Анонимный гость"
                    : string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
                return new MeetupAttendeeResponse(
                    $"attendee-{index + 1}",
                    string.IsNullOrWhiteSpace(displayName) ? "Участник" : displayName,
                    attendance.IsAnonymous || !attendance.OpenToMeet ? null : user?.Username,
                    attendance.IsAnonymous ? null : user?.PhotoUrl,
                    attendance.ArrivalTime,
                    attendance.IsAnonymous,
                    attendance.OpenToMeet,
                    isYou);
            }).ToArray();
            return new MeetupPlanResponse(
                venue.Id,
                venue.Name,
                venue.Type,
                venue.Address,
                venue.Lat,
                venue.Lng,
                venue.OpeningHours,
                venue.Website,
                venue.MapUrl,
                venue.IsFeatured && (venue.FeaturedUntilUtc is null || venue.FeaturedUntilUtc > DateTime.UtcNow),
                venue.PromotionLabel,
                venue.ImageUrl,
                venue.GalleryUrl,
                venue.PreviewPostUrl,
                dateText,
                attendees.Length,
                attendees.Count(item => item.OpenToMeet),
                attendees.Any(item => item.IsYou),
                attendees);
        })
        .OrderByDescending(plan => plan.Featured)
        .ThenBy(plan => plan.Venue, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

        return Ok(new MeetupCatalogResponse(dateText, venueCatalog.CheckedAtUtc, venueCatalog.DataSource, plans));
    }

    [HttpPut("{venueId}/attendance")]
    [EnableRateLimiting("meetup-writes")]
    public async Task<IActionResult> SetAttendance(string venueId, [FromBody] SetMeetupAttendanceRequest request, CancellationToken cancellationToken)
    {
        var user = telegramAuth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (user is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы отметить участие.");
        var meetupDate = ParseDate(request.Date);
        if (meetupDate is null || meetupDate < AstanaToday() || meetupDate > AstanaToday().AddDays(60))
            return BadRequest("Можно отметить участие только на сегодня или на дату в ближайшие 60 дней.");
        if (!TimeOnly.TryParseExact(request.ArrivalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return BadRequest("Укажи примерное время в формате ЧЧ:ММ.");

        var venues = venueCatalog.GetVenues();
        if (!venues.Any(venue => venue.Id == venueId)) return NotFound("Место не найдено в каталоге OpenStreetMap.");

        var appUser = new TelegramUser
        {
            Id = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Username = user.Username,
            PhotoUrl = IsHttpsUrl(user.PhotoUrl) ? user.PhotoUrl : null,
        };
        users.Upsert(appUser);

        var dateText = meetupDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var attendance = await db.MeetupAttendances.FindAsync([venueId, dateText, user.Id], cancellationToken);
        if (attendance is null)
        {
            attendance = new MeetupAttendance { VenueId = venueId, MeetupDate = dateText, TelegramUserId = user.Id };
            db.MeetupAttendances.Add(attendance);
        }
        attendance.ArrivalTime = request.ArrivalTime;
        attendance.IsAnonymous = request.IsAnonymous;
        attendance.OpenToMeet = request.OpenToMeet;
        attendance.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{venueId}/attendance")]
    [EnableRateLimiting("meetup-writes")]
    public async Task<IActionResult> RemoveAttendance(string venueId, [FromQuery] string date, CancellationToken cancellationToken)
    {
        var user = telegramAuth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (user is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы убрать отметку.");
        var meetupDate = ParseDate(date);
        if (meetupDate is null) return BadRequest("Дата должна быть в формате ГГГГ-ММ-ДД.");
        var dateText = meetupDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var attendance = await db.MeetupAttendances.FindAsync([venueId, dateText, user.Id], cancellationToken);
        if (attendance is null) return NoContent();
        db.MeetupAttendances.Remove(attendance);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static DateOnly AstanaToday()
        => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));

    private static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

public sealed record MeetupCatalogResponse(string Date, DateTime SourceUpdatedAtUtc, string DataSource, IReadOnlyList<MeetupPlanResponse> Venues);

public sealed record MeetupVenueOptionResponse(string Id, string Name);

public sealed record MeetupPlanResponse(
    string Id,
    string Venue,
    string Type,
    string Address,
    double? Lat,
    double? Lng,
    string? OpeningHours,
    string? Website,
    string MapUrl,
    bool Featured,
    string? PromotionLabel,
    string? ImageUrl,
    string? GalleryUrl,
    string? PreviewPostUrl,
    string Date,
    int GoingCount,
    int OpenToMeetCount,
    bool UserGoing,
    IReadOnlyList<MeetupAttendeeResponse> Attendees);

public sealed record MeetupAttendeeResponse(
    string Id,
    string Name,
    string? Username,
    string? PhotoUrl,
    string Time,
    bool Anonymous,
    bool OpenToMeet,
    bool IsYou);

public sealed class SetMeetupAttendanceRequest
{
    public string Date { get; set; } = "";
    public string ArrivalTime { get; set; } = "";
    public bool IsAnonymous { get; set; } = true;
    public bool OpenToMeet { get; set; }
}
