using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/interests")]
public sealed class InterestsController(
    TusaMapDbContext db,
    ITelegramAuthService auth,
    IUserStore users,
    IInterestGroupCatalog catalog,
    ITelegramBotService telegramBot) : ControllerBase
{
    private static readonly HashSet<string> ReportReasons = new(StringComparer.Ordinal)
    {
        "spam", "harassment", "impersonation", "unsafe", "other",
    };

    [HttpGet("gatherings/upcoming")]
    public async Task<ActionResult<IReadOnlyList<UpcomingInterestGatheringResponse>>> GetUpcomingGatherings(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var gatherings = await db.InterestGatherings.AsNoTracking()
            .Where(item => item.IsOpen && !item.IsCancelled && item.MeetupDate.CompareTo(today) >= 0)
            .OrderBy(item => item.MeetupDate)
            .ThenBy(item => item.StartTime)
            .Take(6)
            .ToListAsync(cancellationToken);

        var groups = catalog.GetGroups().ToDictionary(group => group.Id, StringComparer.Ordinal);
        gatherings = gatherings.Where(item => groups.ContainsKey(item.InterestId)).ToList();
        var gatheringIds = gatherings.Select(item => item.Id).ToArray();
        var attendanceCounts = await db.InterestGatheringAttendances.AsNoTracking()
            .Where(item => gatheringIds.Contains(item.GatheringId))
            .GroupBy(item => item.GatheringId)
            .Select(group => new { GatheringId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.GatheringId, item => item.Count, cancellationToken);

        return Ok(gatherings.Select(item =>
        {
            var group = groups[item.InterestId];
            return new UpcomingInterestGatheringResponse(
                item.Id, item.InterestId, group.Name, group.Emoji, item.Title, item.MeetupDate,
                item.StartTime, item.Place, item.Details, attendanceCounts.GetValueOrDefault(item.Id));
        }).ToArray());
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InterestGroupResponse>>> Get(CancellationToken cancellationToken)
    {
        var user = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        var userMemberships = user is null
            ? new List<InterestGroupMembership>()
            : await db.InterestGroupMemberships.AsNoTracking()
                .Where(item => item.TelegramUserId == user.Id)
                .ToListAsync(cancellationToken);
        var joined = userMemberships.Select(item => item.InterestId).ToHashSet(StringComparer.Ordinal);
        var notificationPreferences = userMemberships.ToDictionary(item => item.InterestId, item => item.NotifyNewGatherings, StringComparer.Ordinal);
        var counts = await db.InterestGroupMemberships.AsNoTracking()
            .GroupBy(item => item.InterestId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var storedGatherings = await db.InterestGatherings.AsNoTracking()
            .Where(item => !item.IsCancelled && (item.IsOpen || (user != null && item.HostTelegramUserId == user.Id)))
            .OrderBy(item => item.MeetupDate)
            .ThenBy(item => item.StartTime)
            .ToListAsync(cancellationToken);
        var gatherings = storedGatherings
            .Where(item => string.CompareOrdinal(item.MeetupDate, today) >= 0)
            .ToArray();
        var gatheringIds = gatherings.Select(item => item.Id).ToArray();
        var attendanceRows = await db.InterestGatheringAttendances.AsNoTracking()
            .Where(item => gatheringIds.Contains(item.GatheringId))
            .ToListAsync(cancellationToken);
        var unreadGatheringIds = user is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.InterestGatheringNotifications.AsNoTracking()
                .Where(item => item.TelegramUserId == user.Id && item.ReadAt == null && gatheringIds.Contains(item.GatheringId))
                .Select(item => item.GatheringId)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        return Ok(catalog.GetGroups().Select(group => new InterestGroupResponse(
            group.Id, group.Name, group.Description, group.Emoji,
            counts.GetValueOrDefault(group.Id), joined.Contains(group.Id), notificationPreferences.GetValueOrDefault(group.Id),
            gatherings.Where(item => item.InterestId == group.Id).Select(item => new InterestGatheringResponse(
                item.Id, item.InterestId, item.Title, item.MeetupDate, item.StartTime,
                item.Place, item.Details,
                attendanceRows.Count(row => row.GatheringId == item.Id),
                user is not null && attendanceRows.Any(row => row.GatheringId == item.Id && row.TelegramUserId == user.Id),
                user is not null && item.HostTelegramUserId == user.Id,
                item.IsOpen,
                user is not null && joined.Contains(group.Id) && item.IsOpen && item.HostTelegramUserId != user.Id &&
                    unreadGatheringIds.Contains(item.Id) && !attendanceRows.Any(row => row.GatheringId == item.Id && row.TelegramUserId == user.Id))).ToArray())).ToArray());
    }

    [HttpPost("{interestId}/gatherings")]
    [EnableRateLimiting("interest-actions")]
    public async Task<ActionResult<InterestGatheringResponse>> CreateGathering(
        string interestId, [FromBody] CreateInterestGatheringRequest request, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы предложить встречу.");
        if (identity.IsBot) return Forbid();
        if (catalog.Find(interestId) is null) return NotFound("Такого интереса нет в каталоге.");
        var user = users.Upsert(identity);
        if (user.IsSuspended) return Forbid();
        var isMember = await db.InterestGroupMemberships.AnyAsync(
            item => item.InterestId == interestId && item.TelegramUserId == identity.Id, cancellationToken);
        if (!isMember) return Forbid();

        var title = request.Title.Trim();
        var place = request.Place.Trim();
        var details = (request.Details ?? string.Empty).Trim();
        if (title.Length is < 4 or > 90) return BadRequest("Название должно быть от 4 до 90 символов.");
        if (place.Length is < 3 or > 140) return BadRequest("Укажи общественное место встречи (3–140 символов).");
        if (details.Length > 500) return BadRequest("Описание должно быть не длиннее 500 символов.");
        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return BadRequest("Проверь дату встречи.");
        var todayDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        if (date < todayDate || date > todayDate.AddDays(60))
            return BadRequest("Встречу можно назначить на сегодня или в ближайшие 60 дней.");
        if (!TimeOnly.TryParseExact(request.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return BadRequest("Проверь время встречи.");

        var gathering = new InterestGathering
        {
            InterestId = interestId,
            HostTelegramUserId = identity.Id,
            Title = title,
            MeetupDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StartTime = request.Time,
            Place = place,
            Details = details,
            IsOpen = request.IsOpen,
        };
        db.InterestGatherings.Add(gathering);
        db.InterestGatheringAttendances.Add(new InterestGatheringAttendance
        {
            GatheringId = gathering.Id,
            TelegramUserId = identity.Id,
        });
        db.FunnelEvents.Add(new FunnelEvent
        {
            Name = "community_gathering_rsvp",
            Ref = $"gathering:{gathering.Id}",
            TelegramUserId = null,
        });
        List<(long TelegramUserId, bool NotifyTelegram)> recipients = [];
        if (gathering.IsOpen)
        {
            var recipientRows = await db.InterestGroupMemberships.AsNoTracking()
                .Where(item => item.InterestId == interestId && item.TelegramUserId != identity.Id)
                .Join(db.Users.AsNoTracking().Where(item => !item.IsSuspended), membership => membership.TelegramUserId,
                    member => member.TelegramUserId, (membership, member) => new { member.TelegramUserId, membership.NotifyNewGatherings })
                .ToListAsync(cancellationToken);
            recipients = recipientRows.Select(item => (item.TelegramUserId, item.NotifyNewGatherings)).ToList();
            db.InterestGatheringNotifications.AddRange(recipients.Select(recipient => new InterestGatheringNotification
            {
                GatheringId = gathering.Id,
                TelegramUserId = recipient.TelegramUserId,
            }));
        }
        await db.SaveChangesAsync(cancellationToken);

        if (gathering.IsOpen)
        {
            var interest = catalog.Find(interestId)!;
            foreach (var recipient in recipients.Where(item => item.NotifyTelegram))
                await telegramBot.NotifyInterestGatheringAsync(recipient.TelegramUserId, interest.Name, gathering, cancellationToken);
        }

        return StatusCode(StatusCodes.Status201Created, new InterestGatheringResponse(
            gathering.Id, interestId, gathering.Title, gathering.MeetupDate, gathering.StartTime,
            gathering.Place, gathering.Details, 1, true, true, gathering.IsOpen, false));
    }

    [HttpPut("{interestId}/gatherings/{gatheringId}/attendance")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> JoinGathering(string interestId, string gatheringId, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы записаться.");
        if (identity.IsBot) return Forbid();
        var user = users.Upsert(identity);
        if (user.IsSuspended) return Forbid();
        var isMember = await db.InterestGroupMemberships.AnyAsync(
            item => item.InterestId == interestId && item.TelegramUserId == identity.Id, cancellationToken);
        if (!isMember) return Forbid();
        var gathering = await db.InterestGatherings.FirstOrDefaultAsync(
            item => item.Id == gatheringId && item.InterestId == interestId && !item.IsCancelled, cancellationToken);
        if (gathering is null || !gathering.IsOpen) return NotFound("Открытая встреча не найдена или отменена.");
        var date = DateOnly.ParseExact(gathering.MeetupDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (date < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5))) return BadRequest("Эта встреча уже прошла.");

        var attendance = await db.InterestGatheringAttendances.FindAsync([gatheringId, identity.Id], cancellationToken);
        var notification = await db.InterestGatheringNotifications.FindAsync([gatheringId, identity.Id], cancellationToken);
        var changed = false;
        if (attendance is null)
        {
            db.InterestGatheringAttendances.Add(new InterestGatheringAttendance
            {
                GatheringId = gatheringId,
                TelegramUserId = identity.Id,
            });
            db.FunnelEvents.Add(new FunnelEvent
            {
                Name = "community_gathering_rsvp",
                Ref = $"gathering:{gatheringId}",
                TelegramUserId = null,
            });
            changed = true;
        }
        if (notification is not null && notification.ReadAt is null)
        {
            notification.ReadAt = DateTime.UtcNow;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("{interestId}/gatherings/{gatheringId}/notification/read")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> MarkGatheringNotificationRead(string interestId, string gatheringId, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы открыть уведомление.");
        if (identity.IsBot) return Forbid();
        var currentUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.TelegramUserId == identity.Id, cancellationToken);
        if (currentUser?.IsSuspended == true) return Forbid();
        var isMember = await db.InterestGroupMemberships.AnyAsync(
            item => item.InterestId == interestId && item.TelegramUserId == identity.Id, cancellationToken);
        if (!isMember) return Forbid();
        var belongsToGroup = await db.InterestGatherings.AnyAsync(
            item => item.Id == gatheringId && item.InterestId == interestId && item.IsOpen, cancellationToken);
        if (!belongsToGroup) return NotFound();
        var notification = await db.InterestGatheringNotifications.FindAsync([gatheringId, identity.Id], cancellationToken);
        if (notification is not null && notification.ReadAt is null)
        {
            notification.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return NoContent();
    }

    [HttpDelete("{interestId}/gatherings/{gatheringId}/attendance")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> LeaveGathering(string interestId, string gatheringId, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы отменить запись.");
        var attendance = await db.InterestGatheringAttendances.FindAsync([gatheringId, identity.Id], cancellationToken);
        if (attendance is not null)
        {
            var belongsToInterest = await db.InterestGatherings.AnyAsync(
                item => item.Id == gatheringId && item.InterestId == interestId, cancellationToken);
            if (!belongsToInterest) return NotFound();
            db.InterestGatheringAttendances.Remove(attendance);
            await db.SaveChangesAsync(cancellationToken);
        }
        return NoContent();
    }

    [HttpDelete("{interestId}/gatherings/{gatheringId}")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> CancelGathering(string interestId, string gatheringId, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы отменить встречу.");
        if (identity.IsBot) return Forbid();
        var user = users.Upsert(identity);
        if (user.IsSuspended) return Forbid();
        var isMember = await db.InterestGroupMemberships.AnyAsync(
            item => item.InterestId == interestId && item.TelegramUserId == identity.Id, cancellationToken);
        if (!isMember) return Forbid();
        var gathering = await db.InterestGatherings.FirstOrDefaultAsync(
            item => item.Id == gatheringId && item.InterestId == interestId, cancellationToken);
        if (gathering is null) return NoContent();
        if (gathering.HostTelegramUserId != identity.Id) return Forbid();
        gathering.IsCancelled = true;
        var notifications = await db.InterestGatheringNotifications
            .Where(item => item.GatheringId == gatheringId)
            .ToListAsync(cancellationToken);
        db.InterestGatheringNotifications.RemoveRange(notifications);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("{interestId}/members")]
    public async Task<ActionResult<IReadOnlyList<InterestMemberResponse>>> Members(string interestId, CancellationToken cancellationToken)
    {
        if (catalog.Find(interestId) is null) return NotFound();
        var current = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (current is null) return Unauthorized("Открой TUSA внутри Telegram.");
        var currentUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.TelegramUserId == current.Id, cancellationToken);
        if (currentUser?.IsSuspended == true) return Forbid();
        var isMember = await db.InterestGroupMemberships.AnyAsync(
            item => item.InterestId == interestId && item.TelegramUserId == current.Id, cancellationToken);
        if (!isMember) return Forbid();

        var members = await (from membership in db.InterestGroupMemberships.AsNoTracking()
                             join user in db.Users.AsNoTracking() on membership.TelegramUserId equals user.TelegramUserId
                             where membership.InterestId == interestId && !user.IsSuspended
                             orderby membership.JoinedAt
                             select new InterestMemberResponse(
                                 membership.IsAnonymous ? null : user.TelegramUserId,
                                 membership.IsAnonymous ? "Анонимный участник" : string.Join(' ', new[] { user.FirstName, user.LastName }.Where(name => !string.IsNullOrWhiteSpace(name))),
                                 membership.IsAnonymous ? null : user.PhotoUrl,
                                 membership.OpenToMeet && !membership.IsAnonymous ? user.Username : null,
                                 membership.IsAnonymous,
                                 membership.OpenToMeet,
                                 user.TelegramUserId == current.Id))
            .ToListAsync(cancellationToken);
        return Ok(members);
    }

    [HttpPut("{interestId}/membership")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> Join(string interestId, [FromBody] SetInterestMembershipRequest request, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы присоединиться.");
        if (identity.IsBot) return Forbid();
        if (catalog.Find(interestId) is null) return NotFound("Такого интереса нет в каталоге.");
        var user = users.Upsert(identity);
        if (user.IsSuspended) return Forbid();

        var membership = await db.InterestGroupMemberships.FindAsync([interestId, identity.Id], cancellationToken);
        if (membership is null)
        {
            var currentCount = await db.InterestGroupMemberships.CountAsync(item => item.TelegramUserId == identity.Id, cancellationToken);
            if (currentCount >= 5) return BadRequest("Можно выбрать не больше пяти интерес-групп.");
            membership = new InterestGroupMembership { InterestId = interestId, TelegramUserId = identity.Id };
            db.InterestGroupMemberships.Add(membership);
        }
        membership.IsAnonymous = request.IsAnonymous;
        membership.OpenToMeet = request.OpenToMeet && !request.IsAnonymous;
        membership.JoinedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("{interestId}/notifications")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> SetGatheringNotifications(string interestId, [FromBody] SetGatheringNotificationRequest request, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы изменить уведомления.");
        if (identity.IsBot) return Forbid();
        var user = users.Upsert(identity);
        if (user.IsSuspended) return Forbid();
        var membership = await db.InterestGroupMemberships.FindAsync([interestId, identity.Id], cancellationToken);
        if (membership is null) return Forbid();
        membership.NotifyNewGatherings = request.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{interestId}/membership")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> Leave(string interestId, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы выйти из группы.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hostsUpcomingGathering = await db.InterestGatherings.AnyAsync(item =>
            item.InterestId == interestId && item.HostTelegramUserId == identity.Id &&
            !item.IsCancelled && item.MeetupDate.CompareTo(today) >= 0, cancellationToken);
        if (hostsUpcomingGathering)
            return Conflict("Сначала отмени свои будущие встречи, затем выйди из группы.");

        var gatheringIds = await db.InterestGatherings.AsNoTracking()
            .Where(item => item.InterestId == interestId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var attendanceRows = await db.InterestGatheringAttendances
            .Where(item => item.TelegramUserId == identity.Id && gatheringIds.Contains(item.GatheringId))
            .ToListAsync(cancellationToken);
        db.InterestGatheringAttendances.RemoveRange(attendanceRows);
        var notifications = await db.InterestGatheringNotifications
            .Where(item => item.TelegramUserId == identity.Id && gatheringIds.Contains(item.GatheringId))
            .ToListAsync(cancellationToken);
        db.InterestGatheringNotifications.RemoveRange(notifications);

        var membership = await db.InterestGroupMemberships.FindAsync([interestId, identity.Id], cancellationToken);
        if (membership is not null) db.InterestGroupMemberships.Remove(membership);
        if (attendanceRows.Count > 0 || notifications.Count > 0 || membership is not null)
            await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{interestId}/reports")]
    [EnableRateLimiting("interest-actions")]
    public async Task<IActionResult> Report(string interestId, [FromBody] CreateInterestReportRequest request, CancellationToken cancellationToken)
    {
        var identity = auth.ValidateInitData(Request.Headers["X-Telegram-Init-Data"].ToString());
        if (identity is null) return Unauthorized("Открой TUSA внутри Telegram, чтобы отправить жалобу.");
        if (identity.IsBot) return Forbid();
        if (catalog.Find(interestId) is null || !ReportReasons.Contains(request.Reason)) return BadRequest("Проверь интерес и причину жалобы.");
        if (request.TargetTelegramUserId == identity.Id) return BadRequest("Нельзя пожаловаться на себя.");
        var currentMember = await db.InterestGroupMemberships.AnyAsync(
            item => item.InterestId == interestId && item.TelegramUserId == identity.Id, cancellationToken);
        var targetMember = await db.InterestGroupMemberships.AnyAsync(
            item => item.InterestId == interestId && item.TelegramUserId == request.TargetTelegramUserId, cancellationToken);
        if (!currentMember || !targetMember) return Forbid();
        if (await db.InterestUserReports.AnyAsync(report => report.InterestId == interestId &&
                report.ReporterTelegramUserId == identity.Id && report.TargetTelegramUserId == request.TargetTelegramUserId,
                cancellationToken))
            return Conflict("Жалоба на этого участника уже отправлена.");

        db.InterestUserReports.Add(new InterestUserReport
        {
            InterestId = interestId,
            ReporterTelegramUserId = identity.Id,
            TargetTelegramUserId = request.TargetTelegramUserId,
            Reason = request.Reason,
            Details = (request.Details ?? string.Empty).Trim()[..Math.Min((request.Details ?? string.Empty).Trim().Length, 500)],
        });
        await db.SaveChangesAsync(cancellationToken);
        return Accepted();
    }
}

public sealed record InterestGroupResponse(string Id, string Name, string Description, string Emoji, int MemberCount, bool IsMember,
    bool NotifyNewGatherings, IReadOnlyList<InterestGatheringResponse> Gatherings);
public sealed record InterestGatheringResponse(string Id, string InterestId, string Title, string Date,
    string Time, string Place, string Details, int GoingCount, bool UserGoing, bool IsHost, bool IsOpen, bool IsNew);
public sealed record UpcomingInterestGatheringResponse(string Id, string InterestId, string InterestName, string InterestEmoji,
    string Title, string Date, string Time, string Place, string Details, int GoingCount);
public sealed record InterestMemberResponse(long? TelegramUserId, string Name, string? PhotoUrl, string? Username, bool Anonymous, bool OpenToMeet, bool IsYou);
public sealed record SetInterestMembershipRequest(bool IsAnonymous = true, bool OpenToMeet = false);
public sealed record CreateInterestReportRequest(long TargetTelegramUserId, string Reason, string? Details);
public sealed record CreateInterestGatheringRequest(string Title, string Date, string Time, string Place, string? Details, bool IsOpen = true);
public sealed record SetGatheringNotificationRequest(bool Enabled);
