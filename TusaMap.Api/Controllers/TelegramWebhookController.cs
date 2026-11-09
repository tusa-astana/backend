using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/telegram")]
public class TelegramWebhookController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _clients;
    private readonly ITicketsStore _tickets;
    private readonly ITelegramBotService _telegramBot;
    private readonly IEventsStore _events;
    private readonly TusaMapDbContext _db;
    private readonly IUserStore _users;
    private readonly ILogger<TelegramWebhookController> _logger;

    public TelegramWebhookController(IConfiguration configuration, IHttpClientFactory clients, ITicketsStore tickets, ITelegramBotService telegramBot, IEventsStore events, TusaMapDbContext db, IUserStore users, ILogger<TelegramWebhookController> logger)
    {
        _configuration = configuration;
        _clients = clients;
        _tickets = tickets;
        _telegramBot = telegramBot;
        _events = events;
        _db = db;
        _users = users;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Receive([FromBody] TelegramUpdate update, CancellationToken cancellationToken)
    {
        if (!IsValidSecret(Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString())) return Unauthorized();
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token)) return StatusCode(503);

        var message = update.Message;
        if (message is not null)
            await LogAndForwardMessageAsync(update.UpdateId, message, cancellationToken);
        var text = message?.Text?.Trim() ?? "";
        var command = text.Split(' ', 2, StringSplitOptions.TrimEntries)[0].Split('@')[0];
        var argument = text.Contains(' ') ? text[(text.IndexOf(' ') + 1)..].Trim() : "";
        if (message?.From is not null && command.Equals("/terms", StringComparison.OrdinalIgnoreCase))
            await SendTermsAsync(token, message.Chat.Id, cancellationToken);
        else if (message?.From is not null && command.Equals("/paysupport", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(argument))
                await SendMessageAsync(token, message.Chat.Id, "Отправьте описание проблемы и номер заказа одной командой, например: /paysupport не прошла оплата, заказ ABC123. Поддержка Telegram не управляет покупками в сторонних ботах.", cancellationToken);
            else
            {
                var details = argument[..Math.Min(argument.Length, 500)];
                var notified = await _telegramBot.NotifyAdminsOfPaymentSupportAsync(message.From.Id, details, cancellationToken);
                await SendMessageAsync(token, message.Chat.Id,
                    notified ? "Запрос передан команде TUSA. Мы ответим в этом чате." : "Не удалось передать запрос команде. Попробуйте позже или напишите организатору события.",
                    cancellationToken);
            }
        }
        else if (message?.From is not null && command.Equals("/refundstars", StringComparison.OrdinalIgnoreCase))
            await RefundLatestSubscriptionAsync(token, message, argument, cancellationToken);
        else if (message?.From is not null && command.Equals("/refundpromotion", StringComparison.OrdinalIgnoreCase))
            await RefundEventPromotionAsync(token, message, argument, cancellationToken);
        else if (message?.From is not null && text.Equals("/start paysupport", StringComparison.OrdinalIgnoreCase))
            await SendMessageAsync(token, message.Chat.Id, "Чтобы связаться с поддержкой, отправьте /paysupport и описание проблемы с номером заказа в одном сообщении.", cancellationToken);
        else if (message?.From is not null && text.Equals("/start terms", StringComparison.OrdinalIgnoreCase))
            await SendTermsAsync(token, message.Chat.Id, cancellationToken);
        else if (message?.From is not null && text.StartsWith("/start event_", StringComparison.OrdinalIgnoreCase))
            await StartEventPaymentAsync(token, message, text[13..].Trim(), cancellationToken);
        else if (message?.From is not null && text.StartsWith("/start interest_", StringComparison.OrdinalIgnoreCase))
            await RegisterInterestAsync(token, message, text[16..].Trim(), cancellationToken);
        else if (message?.From is not null &&
                 (text.Equals("/start subscribe_pro", StringComparison.OrdinalIgnoreCase) ||
                  command.Equals("/organizerpro", StringComparison.OrdinalIgnoreCase)))
            await StartSubscriptionPaymentAsync(token, message, cancellationToken);
        else if (message?.From is not null && command.Equals("/start", StringComparison.OrdinalIgnoreCase))
            await SendWelcomeAsync(token, message.Chat.Id, cancellationToken);

        if (update.CallbackQuery is { } callback)
        {
            await _clients.CreateClient().PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/answerCallbackQuery",
                new { callback_query_id = callback.Id }, cancellationToken);
            if (callback.From is not null &&
                (callback.Data == "agree_subpro_20260928" || callback.Data == "agree_subpro_20261105") &&
                callback.Message is not null)
                await AcceptSubscriptionTermsAndInvoiceAsync(token, callback, cancellationToken);
            else if (callback.From is not null && callback.Message is not null && callback.Data?.StartsWith("accept_ticket_", StringComparison.Ordinal) == true)
                await AcceptTicketTermsAndInvoiceAsync(token, callback, callback.Data[14..], cancellationToken);
        }

        if (update.PreCheckoutQuery is not null)
        {
            var query = update.PreCheckoutQuery;
            var valid = query.From is not null && (query.InvoicePayload.StartsWith("eventpromo:", StringComparison.OrdinalIgnoreCase)
                ? await IsValidEventPromotionInvoiceAsync(query.InvoicePayload, query.Currency, query.TotalAmount, query.From.Id, cancellationToken)
                : IsValidInvoice(query.InvoicePayload, query.Currency, query.TotalAmount, query.From.Id));
            if (valid && query.InvoicePayload.StartsWith("eventpromo:", StringComparison.OrdinalIgnoreCase))
            {
                var promotionId = query.InvoicePayload["eventpromo:".Length..];
                var promotion = await _db.EventPromotions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == promotionId, cancellationToken);
                if (promotion is not null)
                {
                    _db.FunnelEvents.Add(new FunnelEvent { Name = "promotion_pre_checkout", EventId = promotion.EventId });
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
            if (valid && !query.InvoicePayload.StartsWith("subscription:", StringComparison.OrdinalIgnoreCase) &&
                !query.InvoicePayload.StartsWith("eventpromo:", StringComparison.OrdinalIgnoreCase))
            {
                var ticket = _tickets.GetByPaymentReference(query.InvoicePayload);
                if (ticket is not null)
                {
                    ticket.PaymentStatus = "checkout";
                    ticket.PaymentCheckoutAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
            var errorMessage = query.InvoicePayload.StartsWith("eventpromo:", StringComparison.OrdinalIgnoreCase)
                ? "Продвижение недоступно или сумма/условия не совпадают. Счёт не подтверждён; оплата не выполнена."
                : "Оплата офлайн-билетов в TUSA отключена. Для вопросов по уже оформленному заказу используйте /paysupport.";
            var answer = valid
                ? new { pre_checkout_query_id = query.Id, ok = true, error_message = (string?)null }
                : new { pre_checkout_query_id = query.Id, ok = false, error_message = (string?)errorMessage };
            await _clients.CreateClient().PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/answerPreCheckoutQuery",
                answer, cancellationToken);
        }

        var payment = update.Message?.SuccessfulPayment;
        if (payment is not null)
        {
            var payerId = update.Message!.From?.Id ?? 0;
            if (payment.InvoicePayload.StartsWith("eventpromo:", StringComparison.OrdinalIgnoreCase))
            {
                await ActivateEventPromotionAsync(token, update.Message.Chat.Id, payerId, payment.InvoicePayload,
                    payment.Currency, payment.TotalAmount, payment.TelegramPaymentChargeId, cancellationToken);
            }
            else if (payment.InvoicePayload.StartsWith("subscription:", StringComparison.OrdinalIgnoreCase))
            {
                if (IsValidInvoice(payment.InvoicePayload, payment.Currency, payment.TotalAmount, payerId))
                    await ActivateSubscriptionAsync(token, update.Message.Chat.Id, payment.InvoicePayload, payment.TelegramPaymentChargeId, cancellationToken);
            }
            else
            {
                var existing = _tickets.GetByPaymentReference(payment.InvoicePayload);
                if (existing?.PaymentStatus == "paid" && existing.ProviderPaymentChargeId == payment.ProviderPaymentChargeId)
                    return Ok();
                if (existing is not null && existing.PaymentMethod == "telegram" && payment.Currency == "XTR")
                {
                    var refunded = await _telegramBot.RefundStarPaymentAsync(payerId, payment.TelegramPaymentChargeId, cancellationToken);
                    existing.TelegramPaymentChargeId = payment.TelegramPaymentChargeId;
                    existing.PaymentStatus = refunded ? "refunded" : "refund_requested";
                    existing.RefundStatus = refunded ? "refunded" : "requested";
                    existing.CancelledAt ??= DateTime.UtcNow;
                    await _db.SaveChangesAsync(cancellationToken);
                    if (refunded)
                        await SendMessageAsync(token, payerId, "Офлайн-билет нельзя оплатить Stars. Полученный платёж за старый заказ возвращён.", cancellationToken);
                    else
                    {
                        await _telegramBot.NotifyAdminsOfPaymentSupportAsync(payerId, $"Не удалось автоматически вернуть Stars по устаревшему билету. Charge ID: {payment.TelegramPaymentChargeId}; заказ: {existing.Id}.", cancellationToken);
                        await SendMessageAsync(token, payerId, "Офлайн-билет нельзя оплатить Stars. Автоматический возврат не подтвердился; команда TUSA получила запрос. Напишите /paysupport.", cancellationToken);
                    }
                    return Ok();
                }
                if (existing is not null && existing.PaymentMethod == "telegram_provider" && payment.Currency == "KZT" &&
                    !IsValidInvoice(payment.InvoicePayload, payment.Currency, payment.TotalAmount, payerId))
                {
                    existing.TelegramPaymentChargeId = payment.TelegramPaymentChargeId;
                    existing.ProviderPaymentChargeId = payment.ProviderPaymentChargeId;
                    existing.PaymentStatus = "refund_requested";
                    existing.RefundStatus = "requested";
                    existing.CancelledAt ??= DateTime.UtcNow;
                    await _db.SaveChangesAsync(cancellationToken);
                    await _telegramBot.NotifyAdminsOfPaymentSupportAsync(payerId,
                        $"Оплата provider invoice пришла после истечения резерва или с несовпадающей суммой. Заказ: {existing.Id}; provider charge: {payment.ProviderPaymentChargeId ?? "не указан"}.",
                        cancellationToken);
                    await SendMessageAsync(token, payerId,
                        "Платёж получен, но место уже нельзя подтвердить автоматически. Мы передали запрос команде TUSA для проверки и возврата через платёжного провайдера. Напишите /paysupport, если нужен номер заказа.",
                        cancellationToken);
                    return Ok();
                }
                if (existing is not null && IsValidInvoice(payment.InvoicePayload, payment.Currency, payment.TotalAmount, payerId))
                {
                    var ticket = _tickets.MarkPaid(payment.InvoicePayload, payment.TelegramPaymentChargeId, payment.ProviderPaymentChargeId);
                    if (ticket is not null)
                    {
                        await _telegramBot.SendTicketAsync(ticket, cancellationToken);
                        await _telegramBot.NotifyAdminsOfTicketSaleAsync(ticket, cancellationToken);
                    }
                }
            }
        }

        return Ok();
    }

    private async Task StartEventPaymentAsync(string token, TelegramMessage message, string eventId, CancellationToken cancellationToken)
    {
        await SendMessageAsync(token, message.Chat.Id,
            "TUSA не продаёт билеты на офлайн-события и не принимает оплату за вход. Уточните условия участия у организатора. Напишите /terms или /paysupport.",
            cancellationToken);
    }

    private async Task AcceptTicketTermsAndInvoiceAsync(string token, TelegramCallbackQuery callback, string eventId, CancellationToken cancellationToken)
    {
        await SendMessageAsync(token, callback.Message!.Chat.Id,
            "TUSA не продаёт билеты на офлайн-события. Заказ и списание не создавались.", cancellationToken);
    }

    private async Task SendTermsAsync(string token, long chatId, CancellationToken cancellationToken)
    {
        var termsUrl = _configuration["Telegram:TermsUrl"];
        if (string.IsNullOrWhiteSpace(termsUrl))
            termsUrl = $"{_configuration["Telegram:WebAppUrl"]?.TrimEnd('/')}/terms";
        var text = $"Условия TUSA\n\n" +
                   "TUSA помогает находить события и отмечать интерес, но не продаёт офлайн-билеты и не принимает оплату за вход. Отметка участия не является билетом или бронированием.\n\n" +
                   "Цифровое промо события, если оно включено, оплачивается только Telegram Stars после показа цены и подтверждения условий. Оно не гарантирует охват, RSVP или продажу билетов; билеты продаёт указанный внешний дистрибьютор. Вопросы по оплате и возврату — через /paysupport.\n\n" +
                   "Organizer Pro — отдельная цифровая услуга внутри Telegram. Если подписка включена, цена и условия показываются до оплаты; вопросы и возвраты — через /paysupport.\n\n" +
                   $"Полный текст: {termsUrl}";
        await SendMessageAsync(token, chatId, text, cancellationToken);
    }

    private async Task SendWelcomeAsync(string token, long chatId, CancellationToken cancellationToken)
    {
        var appUrl = (_configuration["Telegram:WebAppUrl"] ?? "https://yessken.github.io").TrimEnd('/');
        var imageUrl = _configuration["Telegram:WelcomeImageUrl"];
        if (string.IsNullOrWhiteSpace(imageUrl)) imageUrl = $"{appUrl}/tusa-avatar.svg";
        var caption = "Привет! Это TUSA — афиша событий и мест Астаны.";
        var replyMarkup = new
        {
            inline_keyboard = new object[][]
            {
                [new { text = "Открыть афишу TUSA", web_app = new { url = $"{appUrl}/events" } }],
            }
        };

        try
        {
            using var response = await _clients.CreateClient().PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendPhoto",
                new { chat_id = chatId, photo = imageUrl, caption, reply_markup = replyMarkup },
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    return;
            }
        }
        catch (HttpRequestException)
        {
            // Fall back to a text-only greeting if the image endpoint is temporarily unavailable.
        }
        catch (JsonException)
        {
            // A malformed response should not prevent the text-only greeting.
        }

        await _clients.CreateClient().PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new { chat_id = chatId, text = caption, reply_markup = replyMarkup },
            cancellationToken);
    }

    private async Task LogAndForwardMessageAsync(long updateId, TelegramMessage message, CancellationToken cancellationToken)
    {
        if (await _db.BotMessageLogs.AnyAsync(x => x.UpdateId == updateId, cancellationToken)) return;

        var user = message.From;
        var knownFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "message_id", "date", "chat", "from", "text", "caption", "entities", "caption_entities",
            "link_preview_options", "successful_payment", "reply_to_message", "forward_origin", "is_topic_message"
        };
        var mediaType = new[] { "photo", "video", "animation", "document", "audio", "voice", "video_note", "sticker", "contact", "location", "venue", "poll", "dice", "game", "story", "web_app_data" }
            .FirstOrDefault(key => message.AdditionalData?.ContainsKey(key) == true);
        var messageType = mediaType ?? (message.SuccessfulPayment is not null ? "successful_payment" : message.Caption is not null ? "media" : "text");
        if (message.AdditionalData?.Keys.Any(key => !knownFields.Contains(key)) == true && mediaType is null)
            messageType = message.AdditionalData.Keys.First(key => !knownFields.Contains(key));
        var content = message.Text ?? message.Caption ??
            (message.SuccessfulPayment is { } payment ? $"Payment: {payment.TotalAmount} {payment.Currency}" : "");
        var senderName = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        var receivedAt = message.Date > 0
            ? DateTimeOffset.FromUnixTimeSeconds(message.Date).UtcDateTime
            : DateTime.UtcNow;

        _db.BotMessageLogs.Add(new BotMessageLog
        {
            UpdateId = updateId,
            MessageId = message.MessageId,
            ChatId = message.Chat.Id,
            TelegramUserId = user?.Id ?? 0,
            SenderName = senderName.Length > 160 ? senderName[..160] : senderName,
            Username = user?.Username,
            MessageType = messageType.Length > 40 ? messageType[..40] : messageType,
            Content = content.Length > 4096 ? content[..4096] : content,
            ReceivedAt = receivedAt,
        });
        await _db.SaveChangesAsync(cancellationToken);

        var admins = _configuration.GetSection("Telegram:AdminUserIds").Get<long[]>() ?? [];
        foreach (var adminId in admins.Distinct().Where(id => id != user?.Id))
        {
            try
            {
                using var response = await _clients.CreateClient().PostAsJsonAsync(
                    $"https://api.telegram.org/bot{_configuration["Telegram:BotToken"]}/forwardMessage",
                    new { chat_id = adminId, from_chat_id = message.Chat.Id, message_id = message.MessageId },
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("Could not forward bot message update {UpdateId} to admin {AdminId}: {StatusCode}", updateId, adminId, response.StatusCode);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Could not forward bot message update {UpdateId} to admin {AdminId}", updateId, adminId);
            }
        }
    }

    private async Task RefundLatestSubscriptionAsync(string token, TelegramMessage message, string argument, CancellationToken cancellationToken)
    {
        var requesterId = message.From!.Id;
        if (!_users.IsAdmin(requesterId))
        {
            await SendMessageAsync(token, message.Chat.Id, "Эта команда доступна только администратору.", cancellationToken);
            return;
        }
        if (!long.TryParse(argument, out var customerId))
        {
            await SendMessageAsync(token, message.Chat.Id, "Формат: /refundstars Telegram_ID_покупателя", cancellationToken);
            return;
        }

        var subscription = await _db.OrganizerSubscriptions.FindAsync([customerId], cancellationToken);
        if (subscription?.LastTelegramChargeId is not { Length: > 0 } chargeId)
        {
            await SendMessageAsync(token, message.Chat.Id, "Для этого пользователя не найдена последняя оплата Organizer Pro.", cancellationToken);
            return;
        }
        if (!await _telegramBot.RefundStarPaymentAsync(customerId, chargeId, cancellationToken))
        {
            await SendMessageAsync(token, message.Chat.Id, "Telegram не подтвердил возврат. Проверьте оплату вручную и повторите запрос при необходимости.", cancellationToken);
            return;
        }

        subscription.Status = "refunded";
        subscription.ExpiresAt = DateTime.UtcNow;
        subscription.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await SendMessageAsync(token, message.Chat.Id, $"Возврат последней оплаты Organizer Pro для пользователя {customerId} подтверждён Telegram.", cancellationToken);
        await SendMessageAsync(token, customerId, "Stars за последнюю оплату Organizer Pro возвращены. Подписка отключена. Если это не решило вопрос, ответьте сюда или используйте /paysupport.", cancellationToken);
    }

    private async Task RefundEventPromotionAsync(string token, TelegramMessage message, string argument, CancellationToken cancellationToken)
    {
        if (!_users.IsAdmin(message.From!.Id))
        {
            await SendMessageAsync(token, message.Chat.Id, "Эта команда доступна только администратору.", cancellationToken);
            return;
        }
        var campaign = await _db.EventPromotions.FirstOrDefaultAsync(item => item.Id == argument && item.Status == "paid", cancellationToken);
        if (campaign?.TelegramPaymentChargeId is not { Length: > 0 } chargeId)
        {
            await SendMessageAsync(token, message.Chat.Id, "Активная оплаченная кампания с таким ID не найдена.", cancellationToken);
            return;
        }
        var refunded = await _telegramBot.RefundStarPaymentAsync(campaign.OrganizerTelegramId, chargeId, cancellationToken);
        if (!refunded)
        {
            await SendMessageAsync(token, message.Chat.Id, "Telegram не подтвердил возврат. Проверьте /paysupport вручную.", cancellationToken);
            return;
        }
        campaign.Status = "refunded";
        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == campaign.EventId, cancellationToken);
        if (ev is not null && ev.FeaturedUntilUtc == campaign.ExpiresAt) ev.FeaturedUntilUtc = DateTime.UtcNow;
        _db.FunnelEvents.Add(new FunnelEvent { Name = "promotion_refunded", EventId = campaign.EventId });
        await _db.SaveChangesAsync(cancellationToken);
        await SendMessageAsync(token, message.Chat.Id, $"Возврат промо-кампании {campaign.Id} подтверждён Telegram.", cancellationToken);
        await SendMessageAsync(token, campaign.OrganizerTelegramId, "Оплата промо возвращена в Telegram Stars; выделенное размещение остановлено.", cancellationToken);
    }

    private async Task<bool> IsValidEventPromotionInvoiceAsync(string payload, string currency, int totalAmount, long payerId, CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Payments:EventPromotionEnabled", false) || !string.Equals(currency, "XTR", StringComparison.Ordinal)) return false;
        var parts = payload.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0] != "eventpromo") return false;
        var campaign = await _db.EventPromotions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == parts[1], cancellationToken);
        if (campaign is null || campaign.Status != "pending" || campaign.OrganizerTelegramId != payerId ||
            campaign.TermsAcceptedAt == default || campaign.TermsVersion != EventPromotionsController.TermsVersion ||
            campaign.StarsAmount != _configuration.GetValue<int>("Payments:EventPromotionStars", 500) || totalAmount != campaign.StarsAmount) return false;
        var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(item => item.Id == campaign.EventId, cancellationToken);
        return ev is not null && ev.Status == "approved" && !ev.IsDemo && !string.IsNullOrWhiteSpace(ev.TicketSellerName) &&
               EventPromotionsController.IsValidTicketUrl(ev.TicketUrl) &&
               DateOnly.TryParseExact(ev.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out var eventDate) && eventDate >= DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
    }

    private async Task ActivateEventPromotionAsync(string token, long chatId, long payerId, string payload, string currency,
        int totalAmount, string chargeId, CancellationToken cancellationToken)
    {
        var parts = payload.Split(':', StringSplitOptions.TrimEntries);
        var campaign = parts.Length == 2
            ? await _db.EventPromotions.FirstOrDefaultAsync(item => item.Id == parts[1], cancellationToken)
            : null;
        if (campaign?.TelegramPaymentChargeId == chargeId && campaign.Status is "paid" or "refunded" or "refund_requested") return;
        if (campaign is null || campaign.Status != "pending" ||
            !await IsValidEventPromotionInvoiceAsync(payload, currency, totalAmount, payerId, cancellationToken))
        {
            var refunded = await _telegramBot.RefundStarPaymentAsync(payerId, chargeId, cancellationToken);
            if (campaign is not null)
            {
                campaign.Status = refunded ? "refunded" : "refund_requested";
                campaign.TelegramPaymentChargeId = chargeId;
                if (refunded) _db.FunnelEvents.Add(new FunnelEvent { Name = "promotion_refunded", EventId = campaign.EventId });
                await _db.SaveChangesAsync(cancellationToken);
            }
            if (!refunded)
                await _telegramBot.NotifyAdminsOfPaymentSupportAsync(payerId, $"Некорректная или просроченная оплата промо; нужен возврат. Charge ID: {chargeId}.", cancellationToken);
            await SendMessageAsync(token, chatId, refunded
                ? "Срок или условия промо уже недоступны; платёж автоматически возвращён в Stars."
                : "Не удалось подтвердить промо. Запрос на проверку/возврат передан в поддержку через /paysupport.", cancellationToken);
            return;
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == campaign.EventId, cancellationToken);
        if (ev is null) return;
        var eventDate = DateOnly.ParseExact(ev.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expiresAt = eventDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-5);
        campaign.Status = "paid";
        campaign.TelegramPaymentChargeId = chargeId;
        campaign.ActivatedAt = DateTime.UtcNow;
        campaign.ExpiresAt = expiresAt;
        ev.FeaturedUntilUtc = expiresAt;
        _db.FunnelEvents.Add(new FunnelEvent { Name = "promotion_paid", EventId = campaign.EventId });
        await _db.SaveChangesAsync(cancellationToken);
        await SendMessageAsync(token, chatId, "Stars-платёж подтверждён. Промо отмечено в TUSA до даты события; билеты продаёт указанный внешний дистрибьютор.", cancellationToken);
        await _telegramBot.NotifyAdminsOfEventPromotionAsync(payerId, ev.Id, campaign.Id, totalAmount, cancellationToken);
    }

    private async Task StartSubscriptionPaymentAsync(string token, TelegramMessage message, CancellationToken cancellationToken)
    {
        var stars = _configuration.GetValue<int>("Payments:TelegramSubscriptionStars");
        if (!_configuration.GetValue("Payments:OrganizerProEnabled", false) || stars <= 0)
        {
            await SendMessageAsync(token, message.Chat.Id, "Подписка организатора пока не настроена.", cancellationToken);
            return;
        }

        var termsUrl = _configuration["Telegram:TermsUrl"];
        if (string.IsNullOrWhiteSpace(termsUrl))
            termsUrl = $"{_configuration["Telegram:WebAppUrl"]?.TrimEnd('/')}/terms";
        await _clients.CreateClient().PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new
        {
            chat_id = message.Chat.Id,
            text = $"Organizer Pro — цифровая подписка на 30 дней за {stars} Stars. Функции активируются после успешной оплаты. Возврат по запросу через /paysupport. Прочитайте условия и подтвердите согласие перед оплатой: {termsUrl}",
            reply_markup = new
            {
                inline_keyboard = new[] { new[] { new { text = "Прочитал и согласен — продолжить", callback_data = "agree_subpro_20261105" } } }
            }
        }, cancellationToken);
    }

    private async Task AcceptSubscriptionTermsAndInvoiceAsync(string token, TelegramCallbackQuery callback, CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Payments:OrganizerProEnabled", false) ||
            _configuration.GetValue<int>("Payments:TelegramSubscriptionStars") <= 0)
        {
            await SendMessageAsync(token, callback.Message!.Chat.Id, "Organizer Pro пока недоступен. Заказ и списание не создавались.", cancellationToken);
            return;
        }

        var user = callback.From!;
        _users.Upsert(user);
        var subscription = await _db.OrganizerSubscriptions.FindAsync([user.Id], cancellationToken)
                           ?? new OrganizerSubscription { TelegramUserId = user.Id };
        subscription.TermsAcceptedAt = DateTime.UtcNow;
        subscription.TermsVersion = "2026-11-05";
        subscription.UpdatedAt = DateTime.UtcNow;
        if (_db.Entry(subscription).State == EntityState.Detached)
            _db.OrganizerSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);

        await SendSubscriptionInvoiceAsync(token, callback.Message!.Chat.Id, user.Id, cancellationToken);
    }

    private async Task SendSubscriptionInvoiceAsync(string token, long chatId, long userId, CancellationToken cancellationToken)
    {
        var stars = _configuration.GetValue<int>("Payments:TelegramSubscriptionStars");
        if (!_configuration.GetValue("Payments:OrganizerProEnabled", false) || stars <= 0) return;
        var payload = $"subscription:{userId}:pro:{stars}";
        var response = await _clients.CreateClient().PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendInvoice", new
        {
            chat_id = chatId,
            title = "TUSA Organizer Pro",
            description = "Доступ к заказам, аналитике и инструментам организатора на 30 дней.",
            payload,
            provider_token = "",
            currency = "XTR",
            prices = new[] { new { label = "Organizer Pro · 30 дней", amount = stars } },
        }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await SendMessageAsync(token, chatId, "Не удалось открыть оплату подписки. Попробуйте позже.", cancellationToken);
    }

    private async Task RegisterInterestAsync(string token, TelegramMessage message, string eventId, CancellationToken cancellationToken)
    {
        var user = message.From!;
        _users.Upsert(user);
        if (_events.GetById(eventId) is null)
        {
            await SendMessageAsync(token, message.Chat.Id, "Событие сейчас недоступно.", cancellationToken);
            return;
        }
        if (!_db.EventInterests.Any(x => x.EventId == eventId && x.TelegramUserId == user.Id))
        {
            _db.EventInterests.Add(new EventInterest { EventId = eventId, TelegramUserId = user.Id });
            await _db.SaveChangesAsync(cancellationToken);
        }
        var count = await _db.EventInterests.CountAsync(x => x.EventId == eventId, cancellationToken);
        var reply = $"Добавил вас в список интересующихся событием ({count}). Это не билет и не бронирование. TUSA не принимает оплату за офлайн-вход; уточняйте условия участия у организатора.";
        await SendMessageAsync(token, message.Chat.Id, reply, cancellationToken);
    }

    private async Task ActivateSubscriptionAsync(string token, long chatId, string payload, string chargeId, CancellationToken cancellationToken)
    {
        var parts = payload.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !long.TryParse(parts[1], out var userId) || parts[2] != "pro") return;
        var subscription = _db.OrganizerSubscriptions.Find(userId) ?? new OrganizerSubscription { TelegramUserId = userId };
        if (subscription.LastTelegramChargeId == chargeId) return;
        subscription.Plan = "pro";
        subscription.Status = "active";
        var now = DateTime.UtcNow;
        subscription.ExpiresAt = (subscription.ExpiresAt is { } expiry && expiry > now ? expiry : now).AddDays(30);
        subscription.UpdatedAt = DateTime.UtcNow;
        subscription.LastTelegramChargeId = chargeId;
        if (_db.Entry(subscription).State == EntityState.Detached) _db.OrganizerSubscriptions.Add(subscription);
        _db.SaveChanges();
        await SendMessageAsync(token, chatId, "Подписка Organizer Pro активирована на 30 дней.", cancellationToken);
        await _telegramBot.NotifyAdminsOfSubscriptionAsync(userId, int.Parse(parts[3]), cancellationToken);
    }

    private bool IsValidInvoice(string payload, string currency, int totalAmount, long payerId)
    {
        if (payload.StartsWith("subscription:", StringComparison.OrdinalIgnoreCase))
        {
            if (!_configuration.GetValue("Payments:OrganizerProEnabled", false) ||
                !string.Equals(currency, "XTR", StringComparison.Ordinal)) return false;
            var parts = payload.Split(':', StringSplitOptions.TrimEntries);
            var consent = long.TryParse(parts.ElementAtOrDefault(1), out var consentUserId)
                ? _db.OrganizerSubscriptions.Find(consentUserId)
                : null;
            return parts.Length == 4 && long.TryParse(parts[1], out var subscriptionUserId) && subscriptionUserId == payerId &&
                consent is not null && (consent.TermsVersion == "2026-09-28" || consent.TermsVersion == "2026-11-05") && consent.TermsAcceptedAt is not null &&
                   parts[2] == "pro" && int.TryParse(parts[3], out var invoiceStars) && invoiceStars > 0 && totalAmount == invoiceStars;
        }

        return false;
    }

    private async Task SendMessageAsync(string token, long chatId, string text, CancellationToken cancellationToken)
    {
        await _clients.CreateClient().PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new { chat_id = chatId, text }, cancellationToken);
    }

    private bool IsValidSecret(string actual)
    {
        var expected = _configuration["Telegram:WebhookSecret"];
        return !string.IsNullOrWhiteSpace(expected) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    }

}

public sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TelegramMessage? Message { get; set; }
    [JsonPropertyName("pre_checkout_query")] public TelegramPreCheckoutQuery? PreCheckoutQuery { get; set; }
    [JsonPropertyName("callback_query")] public TelegramCallbackQuery? CallbackQuery { get; set; }
}

public sealed class TelegramCallbackQuery
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("from")] public TelegramUser? From { get; set; }
    [JsonPropertyName("message")] public TelegramMessage? Message { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
}

public sealed class TelegramMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("date")] public long Date { get; set; }
    [JsonPropertyName("from")] public TelegramUser? From { get; set; }
    [JsonPropertyName("chat")] public TelegramChat Chat { get; set; } = new();
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("successful_payment")] public TelegramSuccessfulPayment? SuccessfulPayment { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class TelegramChat
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

public sealed class TelegramSuccessfulPayment
{
    [JsonPropertyName("invoice_payload")] public string InvoicePayload { get; set; } = "";
    [JsonPropertyName("currency")] public string Currency { get; set; } = "";
    [JsonPropertyName("total_amount")] public int TotalAmount { get; set; }
    [JsonPropertyName("telegram_payment_charge_id")] public string TelegramPaymentChargeId { get; set; } = "";
    [JsonPropertyName("provider_payment_charge_id")] public string? ProviderPaymentChargeId { get; set; }
}

public sealed class TelegramPreCheckoutQuery
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("invoice_payload")] public string InvoicePayload { get; set; } = "";
    [JsonPropertyName("currency")] public string Currency { get; set; } = "";
    [JsonPropertyName("total_amount")] public int TotalAmount { get; set; }
    [JsonPropertyName("from")] public TelegramUser? From { get; set; }
}