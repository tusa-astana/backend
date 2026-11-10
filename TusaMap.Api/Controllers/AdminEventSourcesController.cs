using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/admin/event-sources")]
public sealed class AdminEventSourcesController(
    TusaMapDbContext db,
    ITelegramAuthService auth,
    IUserStore users,
    IInterestGroupCatalog interests) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<IActionResult> Pending([FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        if (!TryGetAdmin(initData)) return StatusCode(StatusCodes.Status403Forbidden);
        var candidates = await db.EventSourceItems.AsNoTracking()
            .Where(item => item.Status == "pending" ||
                (item.Status == "converted" && item.EventId != null &&
                 db.Events.Any(eventItem => eventItem.Id == item.EventId && eventItem.Status == "pending")))
            .OrderByDescending(item => item.PublishedAtUtc)
            .ThenBy(item => item.ImportedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
        return Ok(candidates);
    }

    [HttpPost("{id}/dismiss")]
    public async Task<IActionResult> Dismiss(string id, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        if (!TryGetAdmin(initData)) return StatusCode(StatusCodes.Status403Forbidden);
        var candidate = await db.EventSourceItems.FirstOrDefaultAsync(item => item.Id == id && item.Status == "pending", cancellationToken);
        if (candidate is null) return NotFound();
        candidate.Status = "ignored";
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{id}/convert")]
    public async Task<IActionResult> Convert(
        string id,
        [FromBody] ConvertEventSourceItemRequest request,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdmin(initData)) return StatusCode(StatusCodes.Status403Forbidden);
        var candidate = await db.EventSourceItems.FirstOrDefaultAsync(item => item.Id == id && item.Status == "pending", cancellationToken);
        if (candidate is null) return NotFound();
        if (!request.SourcePermissionConfirmed)
            return BadRequest("Подтвердите проверку источника и разрешение на размещение краткой карточки.");
        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return BadRequest("Укажите дату события в формате ГГГГ-ММ-ДД.");
        var todayAstana = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        if (date < todayAstana || date > todayAstana.AddDays(365))
            return BadRequest("Дата должна быть сегодня или в пределах ближайшего года.");
        var time = request.Time?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(time) && !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return BadRequest("Время должно быть в формате ЧЧ:ММ.");

        var title = request.Title.Trim();
        var place = request.Place.Trim();
        var category = request.Category.Trim();
        var hobbyId = request.HobbyId.Trim();
        if (title.Length is < 3 or > 120 || place.Length is < 2 or > 160 || category.Length is < 2 or > 80 || hobbyId.Length is < 2 or > 60)
            return BadRequest("Проверьте название, место, категорию и хобби.");
        if (interests.Find(candidate.HobbyId) is null || !string.Equals(hobbyId, candidate.HobbyId, StringComparison.Ordinal))
            return BadRequest("Хобби должно совпадать с проверенной категорией RSS-источника.");
        if (interests.Find(hobbyId) is null) return BadRequest("Выберите хобби из каталога TUSA.");
        var description = (request.Description ?? string.Empty).Trim();
        if (description.Length > 3000) return BadRequest("Описание не должно превышать 3000 символов.");

        var eventItem = new EventItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Description = description,
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Time = time,
            Place = place,
            Address = "",
            Category = category,
            HobbyId = hobbyId,
            SourceUrl = candidate.SourceUrl,
            SourceVerifiedAtUtc = DateTime.UtcNow,
            Price = null,
            ImageUrl = "",
            OrganizerName = candidate.SourceName,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            IsPublicEvent = true,
            TelegramSourceUrl = "",
            IsDemo = false,
        };
        candidate.Status = "converted";
        candidate.EventId = eventItem.Id;
        db.Events.Add(eventItem);
        await db.SaveChangesAsync(cancellationToken);
        return Created($"/api/events/{eventItem.Id}", new { eventItem.Id, eventItem.Status, sourceItemId = candidate.Id });
    }

    private bool TryGetAdmin(string? initData)
    {
        var admin = auth.ValidateInitData(initData);
        return admin is not null && users.IsAdmin(admin.Id);
    }
}

public sealed class ConvertEventSourceItemRequest
{
    public string Title { get; set; } = "";
    public string Date { get; set; } = "";
    public string Time { get; set; } = "";
    public string Place { get; set; } = "";
    public string Category { get; set; } = "";
    public string HobbyId { get; set; } = "";
    public string? Description { get; set; }
    public bool SourcePermissionConfirmed { get; set; }
}
