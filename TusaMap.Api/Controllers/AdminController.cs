using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly TusaMapDbContext _db;
    private readonly ITelegramAuthService _auth;
    private readonly IUserStore _users;
    private readonly IInterestGroupCatalog _interests;

    public AdminController(TusaMapDbContext db, ITelegramAuthService auth, IUserStore users, IInterestGroupCatalog interests)
    {
        _db = db;
        _auth = auth;
        _users = users;
        _interests = interests;
    }

    [HttpPost("users/{telegramUserId:long}/role")]
    public IActionResult SetRole(long telegramUserId, [FromBody] SetRoleRequest request, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        if (request.Role is not ("user" or "organizer" or "checker" or "admin")) return BadRequest("Unknown role");
        var target = _db.Users.FirstOrDefault(x => x.TelegramUserId == telegramUserId);
        if (target is null) return NotFound();
        target.Role = request.Role;
        _db.SaveChanges();
        return Ok(new { target.TelegramUserId, target.Role });
    }

    [HttpGet("tickets/refunds")]
    public IActionResult Refunds([FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        return Ok(_db.Tickets.AsNoTracking().Where(x => x.RefundStatus == "requested").ToList());
    }

    [HttpGet("bot-messages")]
    public IActionResult BotMessages(
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        skip = Math.Clamp(skip, 0, 100000);
        take = Math.Clamp(take, 1, 200);
        var messages = _db.BotMessageLogs.AsNoTracking()
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.Id)
            .Skip(skip)
            .Take(take)
            .Select(x => new BotMessageLogRow(x.Id, x.TelegramUserId, x.SenderName, x.Username,
                x.MessageType, x.Content, x.ReceivedAt))
            .ToList();
        return Ok(messages);
    }

    [HttpGet("interest-reports")]
    public async Task<IActionResult> InterestReports([FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        var reports = await (from report in _db.InterestUserReports.AsNoTracking()
                             join reporter in _db.Users.AsNoTracking() on report.ReporterTelegramUserId equals reporter.TelegramUserId
                             join target in _db.Users.AsNoTracking() on report.TargetTelegramUserId equals target.TelegramUserId
                             where report.Status == "pending"
                             orderby report.CreatedAt
                             select new InterestReportRow(
                                 report.Id, report.InterestId, report.Reason, report.Details, report.CreatedAt,
                                 reporter.TelegramUserId, reporter.FirstName, reporter.Username,
                                 target.TelegramUserId, target.FirstName, target.Username, target.IsSuspended))
            .ToListAsync(cancellationToken);
        return Ok(reports.Select(report => report with { InterestName = _interests.Find(report.InterestId)?.Name ?? report.InterestId }));
    }

    [HttpPost("interest-reports/{reportId}/review")]
    public async Task<IActionResult> ReviewInterestReport(string reportId, [FromBody] ReviewInterestReportRequest request,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        if (request.Action is not ("dismiss" or "suspend")) return BadRequest("Action must be dismiss or suspend.");
        var report = await _db.InterestUserReports.FirstOrDefaultAsync(item => item.Id == reportId && item.Status == "pending", cancellationToken);
        if (report is null) return NotFound();
        if (request.Action == "suspend")
        {
            var target = await _db.Users.FirstOrDefaultAsync(user => user.TelegramUserId == report.TargetTelegramUserId, cancellationToken);
            if (target is null) return NotFound("Участник не найден.");
            target.IsSuspended = true;
            target.SuspendedAt = DateTime.UtcNow;
            var memberships = await _db.InterestGroupMemberships.Where(item => item.TelegramUserId == target.TelegramUserId).ToListAsync(cancellationToken);
            _db.InterestGroupMemberships.RemoveRange(memberships);
            var hostedGatherings = await _db.InterestGatherings
                .Where(item => item.HostTelegramUserId == target.TelegramUserId && !item.IsCancelled)
                .ToListAsync(cancellationToken);
            foreach (var gathering in hostedGatherings) gathering.IsCancelled = true;
            var gatheringAttendances = await _db.InterestGatheringAttendances
                .Where(item => item.TelegramUserId == target.TelegramUserId)
                .ToListAsync(cancellationToken);
            _db.InterestGatheringAttendances.RemoveRange(gatheringAttendances);
        }
        report.Status = "reviewed";
        report.ReviewAction = request.Action;
        report.ReviewedAt = DateTime.UtcNow;
        report.ReviewedByTelegramUserId = admin.Id;
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("tickets/by-event")]
    public IActionResult TicketsByEvent([FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        var report = (from ticket in _db.Tickets.AsNoTracking()
                      join ev in _db.Events.AsNoTracking() on ticket.EventId equals ev.Id
                      group ticket by new { ev.Id, ev.Title } into groupByEvent
                      orderby groupByEvent.Key.Title
                      select new
                      {
                          eventId = groupByEvent.Key.Id,
                          eventTitle = groupByEvent.Key.Title,
                          orders = groupByEvent.Count(),
                          tickets = groupByEvent.Sum(x => x.Quantity),
                          paid = groupByEvent.Where(x => x.PaymentStatus == "paid").Sum(x => x.Quantity),
                          pending = groupByEvent.Where(x => x.PaymentStatus == "pending").Sum(x => x.Quantity),
                          revenue = groupByEvent.Where(x => x.PaymentStatus == "paid").Sum(x => x.TotalAmount),
                      }).ToList();
        return Ok(report);
    }

    [HttpGet("sales-summary")]
    public IActionResult SalesSummary([FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        var tickets = _db.Tickets.AsNoTracking();
        return Ok(new
        {
            totalOrders = tickets.Count(),
            totalTickets = tickets.Sum(x => (int?)x.Quantity) ?? 0,
            paidOrders = tickets.Count(x => x.PaymentStatus == "paid"),
            pendingOrders = tickets.Count(x => x.PaymentStatus == "pending"),
            paidRevenue = tickets.Where(x => x.PaymentStatus == "paid").Sum(x => (decimal?)x.TotalAmount) ?? 0,
            requestedRefunds = tickets.Count(x => x.RefundStatus == "requested"),
            activeEvents = _db.Events.Count(x => x.Status == "approved"),
        });
    }

    [HttpPost("organizers/{telegramUserId:long}/subscription")]
    public IActionResult SetSubscription(long telegramUserId, [FromBody] SetSubscriptionRequest request, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var admin = _auth.ValidateInitData(initData);
        if (admin is null || !_users.IsAdmin(admin.Id)) return Forbid();
        if (request.Status is not ("active" or "inactive")) return BadRequest("Status must be active or inactive");
        var subscription = _db.OrganizerSubscriptions.Find(telegramUserId) ?? new Models.OrganizerSubscription { TelegramUserId = telegramUserId };
        subscription.Plan = request.Plan;
        subscription.Status = request.Status;
        subscription.ExpiresAt = request.ExpiresAt;
        subscription.UpdatedAt = DateTime.UtcNow;
        if (_db.Entry(subscription).State == EntityState.Detached) _db.OrganizerSubscriptions.Add(subscription);
        _db.SaveChanges();
        return Ok(subscription);
    }
}

public record SetRoleRequest(string Role);
public record SetSubscriptionRequest(string Plan, string Status, DateTime? ExpiresAt);
public record BotMessageLogRow(long Id, long TelegramUserId, string SenderName, string? Username, string MessageType, string Content, DateTime ReceivedAt);
public record ReviewInterestReportRequest(string Action);
public record InterestReportRow(string Id, string InterestId, string Reason, string Details, DateTime CreatedAt,
    long ReporterTelegramUserId, string ReporterName, string? ReporterUsername,
    long TargetTelegramUserId, string TargetName, string? TargetUsername, bool TargetSuspended,
    string InterestName = "");
