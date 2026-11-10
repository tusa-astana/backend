using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/admin/events")]
public sealed class AdminEventsController(
    TusaMapDbContext db,
    ITelegramAuthService auth,
    IUserStore users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        if (!TryGetAdmin(initData)) return StatusCode(StatusCodes.Status403Forbidden);

        var events = await db.Events.AsNoTracking()
            .Where(item => item.Status == "pending" || item.Status == "approved")
            .OrderByDescending(item => item.Status == "pending")
            .ThenBy(item => item.Date)
            .Take(300)
            .Select(item => new AdminEventEditResponse(
                item.Id, item.Title, item.Description, item.Date, item.Time, item.Place, item.Category, item.ImageUrl, item.Status))
            .ToListAsync(cancellationToken);
        return Ok(events);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdateAdminEventRequest request,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdmin(initData)) return StatusCode(StatusCodes.Status403Forbidden);

        var eventItem = await db.Events.FirstOrDefaultAsync(
            item => item.Id == id && (item.Status == "pending" || item.Status == "approved"), cancellationToken);
        if (eventItem is null) return NotFound();

        var imageUrl = request.ImageUrl?.Trim() ?? "";
        var imageChanged = !string.Equals(imageUrl, eventItem.ImageUrl, StringComparison.Ordinal);
        if (imageChanged && !string.IsNullOrEmpty(imageUrl) && !request.ImagePermissionConfirmed)
            return BadRequest("Подтвердите право на размещение выбранного изображения.");
        if (imageChanged && !EventImageUrlValidator.IsSupported(imageUrl))
            return BadRequest("Выберите WebP-обложку через загрузку или укажите прямую HTTPS-ссылку на изображение.");

        eventItem.Title = request.Title.Trim();
        eventItem.Description = request.Description?.Trim() ?? "";
        eventItem.Date = request.Date;
        eventItem.Time = request.Time?.Trim() ?? "";
        eventItem.Place = request.Place.Trim();
        eventItem.Category = request.Category.Trim();
        eventItem.ImageUrl = imageUrl;

        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(eventItem));
    }

    private bool TryGetAdmin(string? initData)
    {
        var user = auth.ValidateInitData(initData);
        return user is not null && users.IsAdmin(user.Id);
    }

    private static AdminEventEditResponse ToResponse(EventItem item)
        => new(item.Id, item.Title, item.Description, item.Date, item.Time, item.Place, item.Category, item.ImageUrl, item.Status);
}

public sealed class UpdateAdminEventRequest : IValidatableObject
{
    [Required, StringLength(120, MinimumLength = 3)]
    public string Title { get; set; } = "";

    [StringLength(3000)]
    public string? Description { get; set; } = "";

    [Required, StringLength(10)]
    public string Date { get; set; } = "";

    [StringLength(5)]
    public string? Time { get; set; } = "";

    [Required, StringLength(160, MinimumLength = 2)]
    public string Place { get; set; } = "";

    [Required, StringLength(80, MinimumLength = 2)]
    public string Category { get; set; } = "";

    public string? ImageUrl { get; set; } = "";
    public bool ImagePermissionConfirmed { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            yield return new ValidationResult("Дата должна быть в формате ГГГГ-ММ-ДД.", [nameof(Date)]);

        if (!string.IsNullOrWhiteSpace(Time) &&
            !TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            yield return new ValidationResult("Время должно быть в формате ЧЧ:ММ.", [nameof(Time)]);
    }
}

public sealed record AdminEventEditResponse(
    string Id,
    string Title,
    string Description,
    string Date,
    string Time,
    string Place,
    string Category,
    string ImageUrl,
    string Status);
