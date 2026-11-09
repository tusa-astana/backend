using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;

namespace TusaMap.Api.Services;

public sealed class DailyAnalyticsReportWorker : BackgroundService
{
    private static readonly TimeSpan AstanaOffset = TimeSpan.FromHours(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DailyAnalyticsReportWorker> _logger;

    public DailyAnalyticsReportWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<DailyAnalyticsReportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_configuration.GetValue("Analytics:DailyReportEnabled", false))
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }
            if (string.IsNullOrWhiteSpace(_configuration["Telegram:BotToken"]) ||
                (_configuration.GetSection("Telegram:AdminUserIds").Get<long[]>()?.Length ?? 0) == 0)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            var nowUtc = DateTime.UtcNow;
            var nowAstana = nowUtc + AstanaOffset;
            var reportTime = TimeOnly.TryParse(_configuration["Analytics:DailyReportTimeAstana"], CultureInfo.InvariantCulture, out var configuredTime)
                ? configuredTime
                : new TimeOnly(20, 0);
            var reportDate = DateOnly.FromDateTime(nowAstana);
            var reportDateText = reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var scheduledToday = reportDate.ToDateTime(reportTime, DateTimeKind.Unspecified);

            if (nowAstana >= scheduledToday)
            {
                var retrySoon = false;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TusaMapDbContext>();
                    var alreadySent = await db.AdminDailyReportRuns.AsNoTracking()
                        .AnyAsync(item => item.ReportDate == reportDateText, stoppingToken);
                    if (!alreadySent)
                    {
                        var text = await BuildReportAsync(db, reportDate, stoppingToken);
                        var bot = scope.ServiceProvider.GetRequiredService<ITelegramBotService>();
                        if (await bot.NotifyAdminsOfDailyAnalyticsAsync(text, stoppingToken))
                        {
                            db.AdminDailyReportRuns.Add(new AdminDailyReportRun { ReportDate = reportDateText, SentAtUtc = DateTime.UtcNow });
                            await db.SaveChangesAsync(stoppingToken);
                        }
                        else retrySoon = true;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Daily admin analytics report failed for {ReportDate}", reportDateText);
                    retrySoon = true;
                }

                if (retrySoon)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                else
                {
                    var tomorrow = reportDate.AddDays(1).ToDateTime(reportTime, DateTimeKind.Unspecified);
                    var delayUntilTomorrow = tomorrow - nowAstana;
                    await Task.Delay(delayUntilTomorrow > TimeSpan.Zero ? delayUntilTomorrow : TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
            else
            {
                var dueUtc = scheduledToday - AstanaOffset;
                var delay = dueUtc - nowUtc;
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private static async Task<string> BuildReportAsync(TusaMapDbContext db, DateOnly localDate, CancellationToken cancellationToken)
    {
        var dateText = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fromUtc = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-5);
        var throughUtc = fromUtc.AddDays(1);
        var events = await db.FunnelEvents.AsNoTracking()
            .Where(item => item.CreatedAt >= fromUtc && item.CreatedAt < throughUtc)
            .Select(item => new { item.Name, item.Ref, item.EventId })
            .ToListAsync(cancellationToken);

        var openedEventIds = events.Where(item => item.Name == "event_open" && item.EventId is not null)
            .Select(item => item.EventId!).Distinct().ToArray();
        var categories = await db.Events.AsNoTracking()
            .Where(item => openedEventIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Category })
            .ToDictionaryAsync(item => item.Id, item => item.Category, cancellationToken);
        var categoryCounts = events
            .Where(item => item.Name == "event_open" && item.EventId is not null && categories.ContainsKey(item.EventId))
            .GroupBy(item => categories[item.EventId!])
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(item => item.Count).Take(4).ToArray();
        var searchTerms = events
            .Where(item => item.Name == "search_term" && item.Ref is { Length: >= 3 and <= 24 } term && term.All(char.IsLetter))
            .GroupBy(item => item.Ref!)
            .Select(group => (Name: group.Key, Count: group.Count()))
            .Where(item => item.Count >= 3)
            .OrderByDescending(item => item.Count).Take(5).ToArray();

        var table = new (string Label, int Count)[]
        {
            ("Открытия афиши", events.Count(item => item.Name == "catalog_view")),
            ("Открытия событий", events.Count(item => item.Name == "event_open")),
            ("Клики билетного продавца", events.Count(item => item.Name == "ticket_seller_click")),
            ("Открытия приглашений", events.Count(item => item.Name == "meetup_invite_open")),
            ("Намерения RSVP встреч", events.Count(item => item.Name is "community_gathering_rsvp" or "meetup_share_intent")),
            ("Промо: предложения", events.Count(item => item.Name == "promotion_offer_view")),
            ("Промо: счета Stars", events.Count(item => item.Name == "promotion_invoice_created")),
            ("Промо: checkout Telegram", events.Count(item => item.Name == "promotion_pre_checkout")),
            ("Промо: отменено/закрыто", events.Count(item => item.Name == "promotion_invoice_cancelled")),
            ("Промо: успешно оплачено", events.Count(item => item.Name == "promotion_paid")),
            ("Промо: возвраты", events.Count(item => item.Name == "promotion_refunded")),
        };

        var lines = new List<string>
        {
            $"TUSA · ежедневная статистика · {dateText} (Астана)",
            "Показатель                         | Действия",
            "-----------------------------------|---------",
        };
        lines.AddRange(table.Select(item => $"{item.Label,-35} | {item.Count,5}"));
        lines.Add("");
        lines.Add("Категории по открытиям:");
        lines.AddRange(categoryCounts.Length == 0 ? ["— пока нет данных"] : categoryCounts.Select(item => $"• {item.Name}: {item.Count}"));
        lines.Add("");
        lines.Add("Поисковые слова (только от 3 совпадений):");
        lines.AddRange(searchTerms.Length == 0 ? ["— пока нет данных"] : searchTerms.Select(item => $"• {item.Name}: {item.Count}"));
        lines.Add("");
        lines.Add("Показатели — действия, не уникальные люди; клики не равны продажам, RSVP не подтверждает посещение.");
        lines.Add("Графики и периоды: https://t.me/tusa_astana_bot?startapp=admin_analytics");
        return string.Join('\n', lines);
    }
}
