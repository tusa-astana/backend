using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/events/{eventId}/promotion")]
public sealed class EventPromotionsController : ControllerBase
{
    public const string TermsVersion = "2026-11-09";
    private static readonly TimeSpan CheckoutLockTimeout = TimeSpan.FromMinutes(15);
    private readonly TusaMapDbContext _db;
    private readonly ITelegramAuthService _auth;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _clients;

    public EventPromotionsController(TusaMapDbContext db, ITelegramAuthService auth, IConfiguration configuration, IHttpClientFactory clients)
    {
        _db = db;
        _auth = auth;
        _configuration = configuration;
        _clients = clients;
    }

    [HttpGet("offer")]
    public IActionResult Offer(string eventId, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null) return Unauthorized();
        var ev = _db.Events.AsNoTracking().FirstOrDefault(item => item.Id == eventId && item.OrganizerTelegramId == user.Id);
        if (ev is null) return NotFound();

        var stars = _configuration.GetValue<int>("Payments:EventPromotionStars", 500);
        var enabled = _configuration.GetValue("Payments:EventPromotionEnabled", false);
        var campaign = _db.EventPromotions.AsNoTracking().FirstOrDefault(item => item.EventId == eventId);
        var staleCheckout = campaign?.Status == "checkout" && campaign.CreatedAt <= DateTime.UtcNow - CheckoutLockTimeout;
        var canStartCampaign = campaign is null || campaign.Status is "failed" or "cancelled" or "pending" || staleCheckout;
        var dateIsFuture = DateOnly.TryParseExact(ev.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var eventDate)
                           && eventDate >= DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var eligible = stars > 0 && ev.Status == "approved" && !ev.IsDemo && dateIsFuture &&
                       !string.IsNullOrWhiteSpace(ev.TicketSellerName) && IsValidTicketUrl(ev.TicketUrl) && canStartCampaign;
        var available = enabled && eligible;
        if (eligible)
        {
            _db.FunnelEvents.Add(new Models.FunnelEvent { Name = "promotion_offer_view", EventId = eventId, TelegramUserId = null });
            _db.SaveChanges();
        }
        return Ok(new
        {
            available,
            eligible,
            stars = Math.Max(0, stars),
            expiresAtEventDate = true,
            ticketUrl = ev.TicketUrl,
            campaignId = campaign?.Id,
            status = campaign?.Status,
            expiresAt = campaign?.ExpiresAt,
        });
    }

    [HttpPost("invoice")]
    public async Task<IActionResult> CreateInvoice(
        string eventId,
        [FromBody] CreateEventPromotionRequest request,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null) return Unauthorized();
        if (!_configuration.GetValue("Payments:EventPromotionEnabled", false))
            return Conflict("Продвижение событий пока не включено; списание не выполнялось.");
        if (!request.TermsAccepted || request.TermsVersion != TermsVersion)
            return BadRequest("Перед оплатой нужно принять актуальные условия продвижения.");

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId && item.OrganizerTelegramId == user.Id, cancellationToken);
        if (ev is null) return NotFound();
        if (ev.Status != "approved" || ev.IsDemo || string.IsNullOrWhiteSpace(ev.TicketSellerName) || !IsValidTicketUrl(ev.TicketUrl))
            return Conflict("Для продвижения нужны одобренное реальное событие и официальный HTTPS-сайт продажи билетов.");
        if (!DateOnly.TryParseExact(ev.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var eventDate) ||
            eventDate < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)))
            return Conflict("Дата события уже прошла или указана некорректно.");

        var stars = _configuration.GetValue<int>("Payments:EventPromotionStars", 500);
        if (stars <= 0) return Conflict("Цена продвижения не настроена.");
        var existingCampaign = await _db.EventPromotions.FirstOrDefaultAsync(item => item.EventId == eventId, cancellationToken);
        var staleCheckout = existingCampaign?.Status == "checkout" && existingCampaign.CreatedAt <= DateTime.UtcNow - CheckoutLockTimeout;
        if (existingCampaign is not null && existingCampaign.Status is not ("failed" or "cancelled" or "pending") && !staleCheckout)
            return Conflict("Для этого события уже создана кампания продвижения.");

        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token)) return StatusCode(503, "Платёжный бот временно недоступен.");
        var campaign = existingCampaign ?? new Models.EventPromotion();
        if (existingCampaign is null) campaign.Id = Guid.NewGuid().ToString("N");
        campaign.EventId = eventId;
        campaign.OrganizerTelegramId = user.Id;
        campaign.StarsAmount = stars;
        campaign.TermsAcceptedAt = DateTime.UtcNow;
        campaign.TermsVersion = TermsVersion;
        campaign.Status = "pending";
        campaign.CreatedAt = DateTime.UtcNow;
        campaign.TelegramPaymentChargeId = null;
        campaign.InvoiceNonce = Guid.NewGuid().ToString("N");
        campaign.ActivatedAt = null;
        campaign.ExpiresAt = null;
        if (existingCampaign is null) _db.EventPromotions.Add(campaign);
        await _db.SaveChangesAsync(cancellationToken);

        var payload = $"eventpromo:{campaign.Id}:{campaign.InvoiceNonce}";
        try
        {
            using var response = await _clients.CreateClient().PostAsJsonAsync($"https://api.telegram.org/bot{token}/createInvoiceLink", new
            {
                title = "Продвижение события в TUSA",
                description = $"Помеченное промо события «{ev.Title}» до даты проведения. Билеты продаёт внешний дистрибьютор.",
                payload,
                provider_token = "",
                currency = "XTR",
                prices = new[] { new { label = "Промо события до даты проведения", amount = stars } },
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                campaign.Status = "failed";
                await _db.SaveChangesAsync(cancellationToken);
                return StatusCode(502, "Telegram не создал счёт; списания не было.");
            }
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean() ||
                !json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
            {
                campaign.Status = "failed";
                await _db.SaveChangesAsync(cancellationToken);
                return StatusCode(502, "Telegram не создал счёт; списания не было.");
            }
            _db.FunnelEvents.Add(new Models.FunnelEvent { Name = "promotion_invoice_created", EventId = eventId, TelegramUserId = null });
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(new { campaignId = campaign.Id, stars, invoiceUrl = result.GetString() });
        }
        catch (HttpRequestException)
        {
            campaign.Status = "failed";
            await _db.SaveChangesAsync(cancellationToken);
            return StatusCode(502, "Не удалось связаться с Telegram; списания не было.");
        }
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> CancelInvoice(
        string eventId,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData,
        CancellationToken cancellationToken)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null) return Unauthorized();
        var campaign = await _db.EventPromotions.FirstOrDefaultAsync(
            item => item.EventId == eventId && item.OrganizerTelegramId == user.Id && (item.Status == "pending" || item.Status == "checkout"),
            cancellationToken);
        if (campaign is null) return NotFound();
        campaign.Status = "cancelled";
        campaign.InvoiceNonce = null;
        _db.FunnelEvents.Add(new Models.FunnelEvent { Name = "promotion_invoice_cancelled", EventId = eventId });
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public static bool IsValidTicketUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.UserInfo);
}

public sealed class CreateEventPromotionRequest
{
    public bool TermsAccepted { get; set; }
    public string TermsVersion { get; set; } = "";
}
