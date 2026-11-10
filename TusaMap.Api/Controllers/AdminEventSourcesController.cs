using System.Globalization;
using System.ComponentModel.DataAnnotations;
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
        if (description.Length > 0 && !request.DescriptionPermissionConfirmed)
            return BadRequest("Подтвердите право использовать это описание или очистите поле и напишите собственный текст.");
        var imageUrl = request.ImageUrl?.Trim() ?? "";
        if (!string.IsNullOrEmpty(imageUrl) && !request.ImagePermissionConfirmed)
            return BadRequest("Подтвердите право на размещение выбранного изображения.");
        if (!EventImageUrlValidator.IsSupported(imageUrl))
            return BadRequest("Выберите WebP-обложку через загрузку или укажите прямую HTTPS-ссылку на изображение.");

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
            ImageUrl = imageUrl,
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

    [HttpPost("convert-batch")]
    public async Task<IActionResult> ConvertBatch(
        [FromBody] ConvertEventSourceBatchRequest request,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdmin(initData)) return StatusCode(StatusCodes.Status403Forbidden);
        if (!request.SourcePermissionConfirmed)
            return BadRequest("Подтвердите проверку каждого выбранного источника и разрешение на размещение краткой карточки.");

        var ids = request.Items.Select(item => item.CandidateId.Trim()).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            return BadRequest("Список содержит пустые или повторяющиеся публикации.");
        var totalImageDataLength = request.Items.Sum(item => item.ImageUrl?.Length ?? 0);
        if (totalImageDataLength > EventImageUrlValidator.MaxBatchImageDataLength)
            return BadRequest("Общий размер загруженных обложек слишком большой. Отправьте несколько меньших пакетов.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var candidates = await db.EventSourceItems
            .Where(item => item.Status == "pending" && ids.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (candidates.Count != ids.Length)
            return Conflict("Некоторые публикации уже обработаны. Обновите список и повторите пакет.");

        var candidateById = candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var todayAstana = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var drafts = new List<(EventSourceItem Candidate, EventItem Event)>();
        foreach (var item in request.Items)
        {
            var candidate = candidateById[item.CandidateId.Trim()];
            if (!DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date < todayAstana || date > todayAstana.AddDays(365))
                return BadRequest(new { candidateId = candidate.Id, error = "Укажите будущую дату события в пределах ближайшего года." });

            var time = item.Time?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(time) &&
                !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return BadRequest(new { candidateId = candidate.Id, error = "Время должно быть в формате ЧЧ:ММ." });

            var title = item.Title.Trim();
            var place = item.Place.Trim();
            var category = item.Category.Trim();
            if (title.Length is < 3 or > 120 || place.Length is < 2 or > 160 || category.Length is < 2 or > 80)
                return BadRequest(new { candidateId = candidate.Id, error = "Проверьте название, место и категорию." });
            if (interests.Find(candidate.HobbyId) is null)
                return BadRequest(new { candidateId = candidate.Id, error = "Хобби источника отсутствует в каталоге TUSA." });

            var description = (item.Description ?? string.Empty).Trim();
            if (description.Length > 3000)
                return BadRequest(new { candidateId = candidate.Id, error = "Описание не должно превышать 3000 символов." });
            if (description.Length > 0 && !item.DescriptionPermissionConfirmed)
                return BadRequest(new { candidateId = candidate.Id, error = "Подтвердите право использовать это описание или удалите подсказку и напишите собственный текст." });
            var imageUrl = item.ImageUrl?.Trim() ?? "";
            if (!string.IsNullOrEmpty(imageUrl) && !item.ImagePermissionConfirmed)
                return BadRequest(new { candidateId = candidate.Id, error = "Подтвердите право на размещение выбранного изображения." });
            if (!EventImageUrlValidator.IsSupported(imageUrl))
                return BadRequest(new { candidateId = candidate.Id, error = "Выберите WebP-обложку через загрузку или укажите прямую HTTPS-ссылку на изображение." });

            drafts.Add((candidate, new EventItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Description = description,
                Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Time = time,
                Place = place,
                Address = "",
                Category = category,
                HobbyId = candidate.HobbyId,
                SourceUrl = candidate.SourceUrl,
                SourceVerifiedAtUtc = DateTime.UtcNow,
                Price = null,
                ImageUrl = imageUrl,
                OrganizerName = candidate.SourceName,
                Status = "pending",
                CreatedAt = DateTime.UtcNow,
                IsPublicEvent = true,
                TelegramSourceUrl = "",
                IsDemo = false,
            }));
        }

        var claimedCount = await db.EventSourceItems
            .Where(item => item.Status == "pending" && ids.Contains(item.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "converting"), cancellationToken);
        if (claimedCount != ids.Length)
            return Conflict("Часть публикаций уже обработана другим администратором. Обновите список и повторите пакет.");

        foreach (var (candidate, eventItem) in drafts)
        {
            candidate.Status = "converted";
            candidate.EventId = eventItem.Id;
            db.Events.Add(eventItem);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(drafts.Select(item => new { candidateId = item.Candidate.Id, eventId = item.Event.Id, status = item.Event.Status }));
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
    public bool DescriptionPermissionConfirmed { get; set; }
    public string? ImageUrl { get; set; }
    public bool ImagePermissionConfirmed { get; set; }
    public bool SourcePermissionConfirmed { get; set; }
}

public sealed class ConvertEventSourceBatchRequest
{
    public bool SourcePermissionConfirmed { get; set; }

    [Required, MinLength(1), MaxLength(100)]
    public List<ConvertEventSourceBatchItemRequest> Items { get; set; } = [];
}

public sealed class ConvertEventSourceBatchItemRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string CandidateId { get; set; } = "";

    [Required, StringLength(120, MinimumLength = 3)]
    public string Title { get; set; } = "";

    [Required, StringLength(10)]
    public string Date { get; set; } = "";

    [StringLength(5)]
    public string? Time { get; set; } = "";

    [Required, StringLength(160, MinimumLength = 2)]
    public string Place { get; set; } = "";

    [Required, StringLength(80, MinimumLength = 2)]
    public string Category { get; set; } = "";

    [StringLength(3000)]
    public string? Description { get; set; } = "";

    public bool DescriptionPermissionConfirmed { get; set; }

    public string? ImageUrl { get; set; } = "";
    public bool ImagePermissionConfirmed { get; set; }
}
