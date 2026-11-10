using System.Net.Http.Json;
using System.Text.Json;
using TusaMap.Api.Models;

namespace TusaMap.Api.Services;

public interface ITelegramBotService
{
    Task SendTicketAsync(Ticket ticket, CancellationToken cancellationToken = default);
    Task NotifyAdminsOfTicketSaleAsync(Ticket ticket, CancellationToken cancellationToken = default);
    Task NotifyAdminsOfSubscriptionAsync(long telegramUserId, int stars, CancellationToken cancellationToken = default);
    Task NotifyAdminsOfDonationAsync(long telegramUserId, int stars, string chargeId, CancellationToken cancellationToken = default);
    Task NotifyAdminsOfEventPromotionAsync(long telegramUserId, string eventId, string campaignId, int stars, CancellationToken cancellationToken = default);
    Task<bool> NotifyAdminsOfDailyAnalyticsAsync(string text, CancellationToken cancellationToken = default);
    Task NotifyAdminsOfEventSubmissionAsync(EventItem eventItem, CancellationToken cancellationToken = default);
    Task NotifyOrganizerOfEventReviewAsync(EventItem eventItem, bool approved, CancellationToken cancellationToken = default);
    Task<bool> NotifyTicketAvailabilityAsync(long telegramUserId, string eventTitle, string eventUrl, CancellationToken cancellationToken = default);
    Task<bool> NotifyInterestGatheringAsync(long telegramUserId, string interestName, InterestGathering gathering, CancellationToken cancellationToken = default);
    Task<bool> NotifyAdminsOfPaymentSupportAsync(long telegramUserId, string details, CancellationToken cancellationToken = default);
    Task<bool> SendPrivateVenueAddressAsync(long telegramUserId, string eventTitle, string address, string date, string time, CancellationToken cancellationToken = default);
    Task<bool> RefundStarPaymentAsync(long telegramUserId, string chargeId, CancellationToken cancellationToken = default);
    Task ConfigureWebAppAsync(CancellationToken cancellationToken = default);
    Task ConfigureWebhookAsync(CancellationToken cancellationToken = default);
}

public class TelegramBotService : ITelegramBotService
{
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(IHttpClientFactory clients, IConfiguration configuration, ILogger<TelegramBotService> logger)
    {
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendTicketAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token) || ticket.TelegramUserId == 0) return;

        var text = $"Билет TUSA подтверждён\n\n{ticket.EventTitle}\n{ticket.EventDate} · {ticket.EventPlace}\nКатегория: {ticket.TicketCategoryName}\nКоличество: {ticket.Quantity}\nКод билета: {ticket.QrCode}";
        var response = await _clients.CreateClient().PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new { chat_id = ticket.TelegramUserId, text },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Telegram ticket delivery failed for ticket {TicketId}: {StatusCode}", ticket.Id, response.StatusCode);
    }

    public Task NotifyAdminsOfTicketSaleAsync(Ticket ticket, CancellationToken cancellationToken = default)
        => NotifyAdminsAsync($"Новая оплата TUSA\nСобытие: {ticket.EventTitle}\nБилеты: {ticket.Quantity}\nСумма: {ticket.TotalAmount:N0} ₸\nTicket ID: {ticket.Id}", cancellationToken);

    public Task NotifyAdminsOfSubscriptionAsync(long telegramUserId, int stars, CancellationToken cancellationToken = default)
        => NotifyAdminsAsync($"Новая оплата Organizer Pro\nTelegram ID: {telegramUserId}\nСумма: {stars} ⭐\nСрок: 30 дней", cancellationToken);

    public Task NotifyAdminsOfDonationAsync(long telegramUserId, int stars, string chargeId, CancellationToken cancellationToken = default)
        => NotifyAdminsAsync($"Добровольная поддержка разработчика\nTelegram ID: {telegramUserId}\nСумма: {stars} ⭐\nCharge ID: {chargeId}", cancellationToken);

    public Task NotifyAdminsOfEventPromotionAsync(long telegramUserId, string eventId, string campaignId, int stars, CancellationToken cancellationToken = default)
        => NotifyAdminsAsync($"Оплачено продвижение события\nEvent ID: {eventId}\nCampaign ID: {campaignId}\nВозврат: /refundpromotion {campaignId}\nTelegram ID организатора: {telegramUserId}\nСумма: {stars} ⭐", cancellationToken);

    public async Task<bool> NotifyAdminsOfDailyAnalyticsAsync(string text, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        var adminIds = _configuration.GetSection("Telegram:AdminUserIds").Get<long[]>() ?? [];
        if (string.IsNullOrWhiteSpace(token) || adminIds.Length == 0) return false;
        var delivered = false;
        foreach (var adminId in adminIds.Distinct())
        {
            try
            {
                using var response = await _clients.CreateClient().PostAsJsonAsync(
                    $"https://api.telegram.org/bot{token}/sendMessage",
                    new { chat_id = adminId, text }, cancellationToken);
                if (response.IsSuccessStatusCode) delivered = true;
                else _logger.LogWarning("Daily analytics report failed for admin {AdminId}: {StatusCode}", adminId, response.StatusCode);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Daily analytics report transport failed for admin {AdminId}", adminId);
            }
        }
        return delivered;
    }

    public Task NotifyAdminsOfEventSubmissionAsync(EventItem eventItem, CancellationToken cancellationToken = default)
    {
        var appUrl = _configuration["Telegram:WebAppUrl"]?.TrimEnd('/');
        var reviewUrl = string.IsNullOrWhiteSpace(appUrl) ? "" : $"\nОчередь модерации: {appUrl}/admin/event-review";
        var contact = string.Join("\n", new[]
        {
            string.IsNullOrWhiteSpace(eventItem.OrganizerName) ? null : $"Организатор: {eventItem.OrganizerName}",
            string.IsNullOrWhiteSpace(eventItem.OrganizerEmail) ? null : $"Email: {eventItem.OrganizerEmail}",
            string.IsNullOrWhiteSpace(eventItem.OrganizerPhone) ? null : $"Телефон: {eventItem.OrganizerPhone}",
            eventItem.OrganizerTelegramId == 0 ? null : $"Telegram ID: {eventItem.OrganizerTelegramId}",
        }.Where(value => value is not null));
        var text = $"Новая заявка на событие\n\n{eventItem.Title}\n{eventItem.Date} · {eventItem.Time} · {eventItem.Place}\n{eventItem.Category} · {(eventItem.Price is null or 0 ? "бесплатно" : $"{eventItem.Price:N0} ₸")}\nПубличное событие: {(eventItem.IsPublicEvent ? "да" : "нет")}\nИсточник: {eventItem.TelegramSourceUrl}\nID: {eventItem.Id}" +
                   (string.IsNullOrWhiteSpace(contact) ? "" : $"\n\n{contact}") + reviewUrl;
        return NotifyAdminsAsync(text, cancellationToken);
    }

    public async Task NotifyOrganizerOfEventReviewAsync(EventItem eventItem, bool approved, CancellationToken cancellationToken = default)
    {
        if (eventItem.OrganizerTelegramId == 0) return;
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token)) return;
        var appUrl = _configuration["Telegram:WebAppUrl"]?.TrimEnd('/');
        var eventUrl = string.IsNullOrWhiteSpace(appUrl) ? "" : $"\n\nОткрыть TUSA: {appUrl}/events/{Uri.EscapeDataString(eventItem.Id)}";
        var text = approved
            ? $"Хорошие новости: событие «{eventItem.Title}» одобрено и опубликовано в TUSA.{eventUrl}"
            : $"Заявка на событие «{eventItem.Title}» пока не одобрена. Напишите нам, если хотите уточнить детали или подать её повторно.";
        try
        {
            using var response = await _clients.CreateClient().PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = eventItem.OrganizerTelegramId, text }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Organizer review notification failed for event {EventId}: {StatusCode}", eventItem.Id, response.StatusCode);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Organizer review notification transport failed for event {EventId}", eventItem.Id);
        }
    }

    public async Task<bool> NotifyTicketAvailabilityAsync(long telegramUserId, string eventTitle, string eventUrl, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token) || telegramUserId == 0) return false;
        try
        {
            using var response = await _clients.CreateClient().PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new
                {
                    chat_id = telegramUserId,
                    text = $"Билеты появились: {eventTitle}. Откройте событие, чтобы посмотреть доступность и условия покупки.",
                    reply_markup = new { inline_keyboard = new[] { new[] { new { text = "Открыть событие", url = eventUrl } } } },
                }, cancellationToken);
            if (response.IsSuccessStatusCode) return true;
            _logger.LogWarning("Ticket availability notification failed for event {EventTitle} and Telegram user {TelegramUserId}: {StatusCode}", eventTitle, telegramUserId, response.StatusCode);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Ticket availability notification transport failed for Telegram user {TelegramUserId}", telegramUserId);
        }
        return false;
    }

    public async Task<bool> NotifyInterestGatheringAsync(long telegramUserId, string interestName, InterestGathering gathering, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token) || telegramUserId == 0) return false;
        var appUrl = _configuration["Telegram:WebAppUrl"]?.TrimEnd('/');
        object? replyMarkup = string.IsNullOrWhiteSpace(appUrl) ? null : new
        {
            inline_keyboard = new[]
            {
                new[] { new { text = "Открыть встречу", web_app = new { url = $"{appUrl}/interests?gathering={Uri.EscapeDataString(gathering.Id)}" } } },
            },
        };
        var text = $"Новая открытая встреча по интересу «{interestName}»\n\n{gathering.Title}\n{gathering.MeetupDate} · {gathering.StartTime}\n{gathering.Place}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _clients.CreateClient().PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = telegramUserId, text, reply_markup = replyMarkup }, timeout.Token);
            if (response.IsSuccessStatusCode) return true;
            _logger.LogWarning("Interest gathering notification failed: {StatusCode}", response.StatusCode);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(exception, "Interest gathering notification timed out or was cancelled");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Interest gathering notification transport failed");
        }
        return false;
    }

    public async Task<bool> NotifyAdminsOfPaymentSupportAsync(long telegramUserId, string details, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        var adminIds = _configuration.GetSection("Telegram:AdminUserIds").Get<long[]>() ?? [];
        if (string.IsNullOrWhiteSpace(token) || adminIds.Length == 0) return false;
        var delivered = false;
        foreach (var adminId in adminIds.Distinct())
        {
            try
            {
                var response = await _clients.CreateClient().PostAsJsonAsync(
                    $"https://api.telegram.org/bot{token}/sendMessage",
                    new { chat_id = adminId, text = $"Запрос /paysupport\nTelegram ID: {telegramUserId}\nСообщение: {details}" }, cancellationToken);
                delivered |= response.IsSuccessStatusCode;
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Payment support notification failed for admin {AdminId}", adminId);
            }
        }
        return delivered;
    }

    private async Task NotifyAdminsAsync(string text, CancellationToken cancellationToken)
    {
        var token = _configuration["Telegram:BotToken"];
        var adminIds = _configuration.GetSection("Telegram:AdminUserIds").Get<long[]>() ?? [];
        if (string.IsNullOrWhiteSpace(token) || adminIds.Length == 0) return;

        foreach (var adminId in adminIds.Distinct())
        {
            try
            {
                var response = await _clients.CreateClient().PostAsJsonAsync(
                    $"https://api.telegram.org/bot{token}/sendMessage",
                    new { chat_id = adminId, text }, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("Admin notification failed for admin {AdminId}: {StatusCode}", adminId, response.StatusCode);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Admin notification transport failed for admin {AdminId}", adminId);
            }
        }
    }

    public async Task<bool> SendPrivateVenueAddressAsync(long telegramUserId, string eventTitle, string address, string date, string time, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token) || telegramUserId == 0) return false;
        var text = $"Адрес события TUSA 2026\n\n{eventTitle}\n{date} · {time}\n\nМесто проведения: {address}\n\nЭто сообщение отправлено только пользователям с оплаченным билетом. Покажите билет/QR на входе.";
        var response = await _clients.CreateClient().PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new { chat_id = telegramUserId, text }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Private venue delivery failed for Telegram user {TelegramUserId}: {StatusCode}", telegramUserId, response.StatusCode);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RefundStarPaymentAsync(long telegramUserId, string chargeId, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token) || telegramUserId == 0 || string.IsNullOrWhiteSpace(chargeId)) return false;
        var response = await _clients.CreateClient().PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/refundStarPayment",
            new { user_id = telegramUserId, telegram_payment_charge_id = chargeId }, cancellationToken);
        var body = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            : default;
        var refunded = response.IsSuccessStatusCode && body.ValueKind == JsonValueKind.Object &&
                       body.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        if (!refunded)
            _logger.LogWarning("Stars refund failed for Telegram user {TelegramUserId}: {StatusCode}", telegramUserId, response.StatusCode);
        return refunded;
    }

    public async Task ConfigureWebAppAsync(CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        var webAppUrl = _configuration["Telegram:WebAppUrl"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(webAppUrl)) return;

        var response = await _clients.CreateClient().PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/setChatMenuButton",
            new
            {
                menu_button = new
                {
                    type = "web_app",
                    text = "Открыть TUSA",
                    web_app = new { url = webAppUrl },
                },
            }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Telegram Web App menu configuration failed: {StatusCode}", response.StatusCode);
    }

    public async Task ConfigureWebhookAsync(CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        var webhookUrl = _configuration["Telegram:WebhookUrl"];
        var secret = _configuration["Telegram:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(webhookUrl) || string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Telegram webhook is not configured: BotToken, WebhookUrl and WebhookSecret are required");
            return;
        }

        var response = await _clients.CreateClient().PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/setWebhook",
            new { url = webhookUrl, secret_token = secret, allowed_updates = new[] { "message", "pre_checkout_query", "callback_query" } },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Telegram webhook configuration failed: {StatusCode}", response.StatusCode);

        var commandsResponse = await _clients.CreateClient().PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/setMyCommands",
            new
            {
                commands = new[]
                {
                    new { command = "start", description = "Открыть TUSA" },
                    new { command = "donate", description = "Поддержать разработчика Stars" },
                    new { command = "terms", description = "Условия сервиса и покупки" },
                    new { command = "paysupport", description = "Помощь с оплатой и возвратом" },
                    new { command = "organizerpro", description = "Подписка для организаторов" },
                }
            }, cancellationToken);
        if (!commandsResponse.IsSuccessStatusCode)
            _logger.LogWarning("Telegram bot command configuration failed: {StatusCode}", commandsResponse.StatusCode);
    }
}
