using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using TusaMap.Api.Models;
using TusaMap.Api.Services;
using TusaMap.Api.Data;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EventsController : ControllerBase
{
    private readonly IEventsStore _store;
    private readonly ITelegramAuthService _telegramAuth;
    private readonly IUserStore _users;
    private readonly TusaMapDbContext _db;
    private readonly ITelegramBotService _telegramBot;
    private readonly IConfiguration _configuration;

    public EventsController(IEventsStore store, ITelegramAuthService telegramAuth, IUserStore users, TusaMapDbContext db, ITelegramBotService telegramBot, IConfiguration configuration)
    {
        _store = store;
        _telegramAuth = telegramAuth;
        _users = users;
        _db = db;
        _telegramBot = telegramBot;
        _configuration = configuration;
    }

    [HttpGet]
    public ActionResult<IEnumerable<EventItem>> Get([FromQuery] string? category = null, [FromQuery] string? q = null)
    {
        var todayInAstana = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var list = _store.GetAll()
            .Where(eventItem => DateOnly.TryParseExact(eventItem.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var eventDate)
                && eventDate >= todayInAstana)
            .ToList();
        if (!string.IsNullOrEmpty(category))
            list = list.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var query = q.Trim();
            list = list.Where(e => new[] { e.Title, e.Place, e.Address, e.Description, e.Category }
                .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        foreach (var eventItem in list)
        {
            SetTicketSalesAvailability(eventItem);
            RemovePrivateOrganizerContacts(eventItem);
        }
        return Ok(list);
    }

    [HttpGet("{id}")]
    public ActionResult<EventItem> GetById(string id)
    {
        var e = _store.GetById(id);
        if (e == null)
            return NotFound();
        SetTicketSalesAvailability(e);
        RemovePrivateOrganizerContacts(e);
        return Ok(e);
    }

    [HttpGet("{id}/going")]
    public ActionResult<EventGoingResponse> GetGoing(string id, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        if (_store.GetById(id) is null) return NotFound();
        var user = _telegramAuth.ValidateInitData(initData);
        var goingCount = _db.EventParticipations.Count(x => x.EventId == id);
        var userGoing = user is not null && _db.EventParticipations.Any(x => x.EventId == id && x.TelegramUserId == user.Id);
        return Ok(new EventGoingResponse(goingCount, userGoing));
    }

    [HttpPost("{id}/going")]
    [EnableRateLimiting("interest-actions")]
    public ActionResult<EventGoingResponse> ToggleGoing(string id, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var user = _telegramAuth.ValidateInitData(initData);
        if (user is null) return Unauthorized("Откройте TUSA из Telegram, чтобы отметить участие.");
        var eventItem = _store.GetById(id);
        if (eventItem is null) return NotFound();
        if (eventItem.IsDemo) return BadRequest("Для демонстрационных событий отметка участия отключена.");

        var participation = _db.EventParticipations.Find(id, user.Id);
        if (participation is null)
            _db.EventParticipations.Add(new EventParticipation { EventId = id, TelegramUserId = user.Id });
        else
            _db.EventParticipations.Remove(participation);
        _db.SaveChanges();

        var response = new EventGoingResponse(
            _db.EventParticipations.Count(x => x.EventId == id),
            participation is null);
        return Ok(response);
    }

    [HttpPost]
    public async Task<ActionResult<EventItem>> Create([FromBody] CreateEventRequest req, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var user = _telegramAuth.ValidateInitData(initData);
        if (user == null)
            return Unauthorized("Invalid or missing Telegram initData");
        _users.Upsert(user);
        return await AddEventAsync(req, user.Id, cancellationToken);
    }

    [HttpPost("public")]
    [EnableRateLimiting("public-event-submissions")]
    public async Task<ActionResult<EventItem>> CreatePublic([FromBody] CreateEventRequest req, CancellationToken cancellationToken)
    {
        return await AddEventAsync(req, 0, cancellationToken);
    }

    private async Task<ActionResult<EventItem>> AddEventAsync(CreateEventRequest req, long organizerTelegramId, CancellationToken cancellationToken)
    {
        var e = new EventItem
        {
            Title = req.Title,
            Description = req.Description ?? "",
            Date = req.Date,
            Time = req.Time ?? "",
            Place = req.Place,
            Address = req.Address ?? "",
            Lat = req.Lat,
            Lng = req.Lng,
            Category = req.Category ?? "концерт",
            Price = req.Price,
            ImageUrl = req.ImageUrl ?? "",
            OrganizerName = req.OrganizerName ?? ""
            ,OrganizerEmail = req.OrganizerEmail ?? ""
            ,OrganizerPhone = req.OrganizerPhone ?? ""
            ,OrganizerTelegramId = organizerTelegramId
        };
        var created = _store.Add(e);
        await _telegramBot.NotifyAdminsOfEventSubmissionAsync(created, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    private void SetTicketSalesAvailability(EventItem eventItem)
    {
        eventItem.TicketSalesEnabled = false;
    }

    private static void RemovePrivateOrganizerContacts(EventItem eventItem)
    {
        eventItem.OrganizerEmail = "";
        eventItem.OrganizerPhone = "";
        eventItem.OrganizerTelegramId = 0;
    }

    [HttpGet("pending")]
    public ActionResult<IEnumerable<EventItem>> Pending([FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var user = _telegramAuth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return Forbid();
        return Ok(_store.GetPending());
    }

    [HttpPost("{id}/approve")]
    public async Task<ActionResult<EventItem>> Approve(string id, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var user = _telegramAuth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return Forbid();
        var approved = _store.Approve(id);
        if (approved is not null)
        {
            await _telegramBot.NotifyOrganizerOfEventReviewAsync(approved, approved: true, cancellationToken);
        }
        return approved is null ? NotFound() : Ok(approved);
    }

    [HttpPost("{id}/reject")]
    public async Task<ActionResult<EventItem>> Reject(string id, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var user = _telegramAuth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return Forbid();
        var rejected = _store.Reject(id);
        if (rejected is not null)
            await _telegramBot.NotifyOrganizerOfEventReviewAsync(rejected, approved: false, cancellationToken);
        return rejected is null ? NotFound() : Ok(rejected);
    }
}

public class CreateEventRequest : IValidatableObject
{
    [Required, StringLength(120, MinimumLength = 3)]
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    [Required, StringLength(10)]
    public string Date { get; set; } = "";
    public string? Time { get; set; }
    [Required, StringLength(160, MinimumLength = 2)]
    public string Place { get; set; } = "";
    public string? Address { get; set; }
    [Range(-90, 90)]
    public double Lat { get; set; }
    [Range(-180, 180)]
    public double Lng { get; set; }
    public string? Category { get; set; }
    [Range(0, 100000000)]
    public decimal? Price { get; set; }
    public string? ImageUrl { get; set; }
    public string? OrganizerName { get; set; }
    [EmailAddress, StringLength(254)]
    public string? OrganizerEmail { get; set; }
    [Phone, StringLength(30)]
    public string? OrganizerPhone { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(OrganizerEmail) && string.IsNullOrWhiteSpace(OrganizerPhone))
            yield return new ValidationResult("Укажите email или телефон, чтобы TUSA могла связаться по заявке.", [nameof(OrganizerEmail), nameof(OrganizerPhone)]);
    }
}

public record EventGoingResponse(int GoingCount, bool UserGoing);
