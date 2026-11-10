using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;

namespace TusaMap.Api.Services;

public sealed class EventSourceFeedWorker : BackgroundService
{
    private const int MaxFeedBytes = 1_500_000;
    private const int MaxItemsPerFeed = 50;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecentPublishedWindow = TimeSpan.FromDays(120);
    private static readonly Regex HtmlTags = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlBlockTags = new("</?(?:p|br|div|li|h[1-6])\\b[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RussianEventDate = new(
        @"(?<!\d)(?<day>\d{1,2})(?:\s*[–—-]\s*\d{1,2})?\s+(?<month>января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря)(?:\s+(?<year>20\d{2}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EnglishEventDate = new(
        @"\b(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<day>\d{1,2})(?:\s+(?:to|[-–—])\s*\d{1,2})?(?:,?\s*(?<year>20\d{2}))?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EventTime = new(
        @"(?<!\d)(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitEventPlace = new(
        @"(?:место(?:\s+проведения)?|площадка|venue|location|hall)\s*[:—-]\s*(?<place>[^\r\n.;!?]{2,120}?)(?=\s+(?:уважаемые|дорогие|приглашаем|приходите|ждём|welcome|dear|join)\b|[.!?;\r\n]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, int> RussianMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["января"] = 1, ["февраля"] = 2, ["марта"] = 3, ["апреля"] = 4,
        ["мая"] = 5, ["июня"] = 6, ["июля"] = 7, ["августа"] = 8,
        ["сентября"] = 9, ["октября"] = 10, ["ноября"] = 11, ["декабря"] = 12,
    };
    private static readonly Dictionary<string, int> EnglishMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["January"] = 1, ["February"] = 2, ["March"] = 3, ["April"] = 4,
        ["May"] = 5, ["June"] = 6, ["July"] = 7, ["August"] = 8,
        ["September"] = 9, ["October"] = 10, ["November"] = 11, ["December"] = 12,
    };
    private static readonly string[] DefaultEventKeywords = ["championship", "competition", "tournament", "cup", "schedule", "чемпионат", "соревн", "турнир", "кубок", "расписани"];
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EventSourceFeedWorker> _logger;

    public EventSourceFeedWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory clients,
        IConfiguration configuration,
        ILogger<EventSourceFeedWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("EventSources:Enabled", false)) return;

        await Task.Delay(InitialDelay, stoppingToken);
        var intervalMinutes = Math.Clamp(_configuration.GetValue("EventSources:PollIntervalMinutes", 60), 15, 1440);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        do
        {
            try
            {
                await SyncConfiguredFeedsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Event source feed synchronization failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncConfiguredFeedsAsync(CancellationToken cancellationToken)
    {
        var sources = _configuration.GetSection("EventSources:Feeds").Get<EventSourceFeedConfig[]>() ?? [];
        foreach (var source in sources.Where(IsAllowedFeed))
        {
            try
            {
                var items = await FetchFeedAsync(source, cancellationToken);
                var allowedHosts = source.AllowedHosts ?? [];
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TusaMapDbContext>();
                var recentSince = DateTime.UtcNow.Subtract(RecentPublishedWindow);
                var todayAstana = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
                var eligible = items
                    .Where(item => item.PublishedAtUtc is null || item.PublishedAtUtc >= recentSince)
                    .Where(item => Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var itemUri) &&
                                   allowedHosts.Any(host => string.Equals(itemUri.Host, host, StringComparison.OrdinalIgnoreCase)))
                    .Where(item => !source.RequireAstanaMention || MentionsAstana(item.Title + " " + item.Summary))
                    .Where(item => !source.RequireEventKeywords || LooksLikeEventNotice(item.Title + " " + item.Summary, source.EventKeywords))
                    .Where(item => !source.RequireFutureEventDate || item.EventDate is { } eventDate && eventDate >= todayAstana)
                    .DistinctBy(item => item.ExternalId, StringComparer.Ordinal)
                    .Take(MaxItemsPerFeed)
                    .ToArray();
                if (eligible.Length == 0) continue;

                var sourceIds = eligible.Select(item => item.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
                var knownItems = await db.EventSourceItems
                    .Where(item => item.SourceId == source.Id && sourceIds.Contains(item.ExternalId))
                    .ToListAsync(cancellationToken);
                var knownByExternalId = knownItems.ToDictionary(item => item.ExternalId, StringComparer.Ordinal);

                foreach (var item in eligible)
                {
                    if (knownByExternalId.TryGetValue(item.ExternalId, out var existing))
                    {
                        if (existing.Status == "pending")
                        {
                            if (!string.IsNullOrWhiteSpace(item.EventTime) && !string.Equals(existing.EventTime, item.EventTime, StringComparison.Ordinal)) existing.EventTime = item.EventTime;
                            if (!string.IsNullOrWhiteSpace(item.EventPlace) && !string.Equals(existing.EventPlace, item.EventPlace, StringComparison.Ordinal)) existing.EventPlace = item.EventPlace;
                            if (!string.IsNullOrWhiteSpace(item.SuggestedCategory) && !string.Equals(existing.SuggestedCategory, item.SuggestedCategory, StringComparison.Ordinal)) existing.SuggestedCategory = item.SuggestedCategory;
                            if (string.IsNullOrWhiteSpace(existing.Summary) && !string.IsNullOrWhiteSpace(item.Summary)) existing.Summary = item.Summary;
                        }
                        continue;
                    }

                    db.EventSourceItems.Add(new EventSourceItem
                    {
                        SourceId = source.Id,
                        SourceName = source.Name,
                        ExternalId = item.ExternalId,
                        SourceUrl = item.SourceUrl,
                        Title = item.Title,
                        Summary = item.Summary,
                        HobbyId = source.HobbyId,
                        EventDate = item.EventDate,
                        EventTime = item.EventTime,
                        EventPlace = item.EventPlace,
                        SuggestedCategory = item.SuggestedCategory,
                        PublishedAtUtc = item.PublishedAtUtc,
                    });
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException exception)
                {
                    // A second API instance may have inserted the same feed item concurrently.
                    _logger.LogInformation(exception, "Duplicate event source items were skipped for {SourceId}", source.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not sync event source {SourceId}", source.Id);
            }
        }
    }

    private async Task<IReadOnlyList<ParsedFeedItem>> FetchFeedAsync(EventSourceFeedConfig source, CancellationToken cancellationToken)
    {
        if (string.Equals(source.Format, "astana-opera-json", StringComparison.OrdinalIgnoreCase))
            return await FetchAstanaOperaPostersAsync(source, cancellationToken);

        var uri = new Uri(source.FeedUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("TUSA-EventSourceReader/1.0 (+https://yessken.github.io/terms)");
        request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml;q=0.9");
        using var response = await _clients.CreateClient("event-source-rss")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Event source {SourceId} returned HTTP {StatusCode}", source.Id, response.StatusCode);
            return [];
        }
        if (response.Content.Headers.ContentLength is > MaxFeedBytes)
            throw new InvalidDataException("RSS feed exceeds the configured size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new LimitedReadStream(stream, MaxFeedBytes);
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxFeedBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(limited, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        return document.Descendants().Where(element => element.Name.LocalName == "item")
            .Take(MaxItemsPerFeed)
            .Select(element => ParseItem(element, source))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }

    private async Task<IReadOnlyList<ParsedFeedItem>> FetchAstanaOperaPostersAsync(EventSourceFeedConfig source, CancellationToken cancellationToken)
    {
        var items = new List<ParsedFeedItem>();
        var pageCount = Math.Clamp(source.PageCount, 1, 5);
        for (var page = 1; page <= pageCount; page++)
        {
            var builder = new UriBuilder(source.FeedUrl) { Query = $"page={page}&lang=ru&limit=12" };
            using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            request.Headers.UserAgent.ParseAdd("TUSA-EventSourceReader/1.0 (+https://yessken.github.io/terms)");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _clients.CreateClient("event-source-rss")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Astana Opera calendar returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxFeedBytes)
                throw new InvalidDataException("Astana Opera calendar response exceeds the configured size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var limited = new LimitedReadStream(stream, MaxFeedBytes);
            using var document = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("data", out var envelope) ||
                !envelope.TryGetProperty("data", out var posters) || posters.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Astana Opera calendar response has an unexpected shape.");

            foreach (var poster in posters.EnumerateArray())
            {
                if (!poster.TryGetProperty("id", out var idElement) ||
                    !poster.TryGetProperty("title", out var titleElement) ||
                    !poster.TryGetProperty("event", out var eventElement) ||
                    eventElement.ValueKind != JsonValueKind.Object ||
                    !eventElement.TryGetProperty("event_date", out var eventDateElement)) continue;

                var id = idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : idElement.ValueKind == JsonValueKind.Number ? idElement.GetRawText() : null;
                var title = CleanText(titleElement.ValueKind == JsonValueKind.String ? titleElement.GetString() : null, 180);
                var dateText = eventDateElement.GetString();
                var timeText = eventElement.TryGetProperty("event_time", out var timeElement) && timeElement.ValueKind == JsonValueKind.String
                    ? timeElement.GetString()
                    : null;
                var hallName = eventElement.TryGetProperty("hall_name", out var hallElement) && hallElement.ValueKind == JsonValueKind.String
                    ? hallElement.GetString()
                    : null;
                var sceneName = poster.TryGetProperty("scene_name", out var sceneElement) && sceneElement.ValueKind == JsonValueKind.String
                    ? sceneElement.GetString()
                    : null;
                var repertoire = poster.TryGetProperty("repertoire_name", out var repertoireElement) && repertoireElement.ValueKind == JsonValueKind.String
                    ? repertoireElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(id) || title.Length < 3 ||
                    !DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var eventDate)) continue;

                var eventTime = TryParseEventTime(timeText);
                var venue = CleanText(!string.IsNullOrWhiteSpace(sceneName) ? sceneName : hallName, 120);
                var eventPlace = venue.Length == 0 ? "Астана Опера" : $"Астана Опера, {venue}";
                var suggestedCategory = MapOperaCategory(repertoire, title);

                var urlTemplate = string.IsNullOrWhiteSpace(source.ItemUrlTemplate)
                    ? "https://astanaopera.kz/ru/afisha/{id}"
                    : source.ItemUrlTemplate;
                items.Add(new ParsedFeedItem(
                    id,
                    urlTemplate.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal),
                    title,
                    "",
                    eventDate,
                    null,
                    eventTime,
                    eventPlace,
                    suggestedCategory));
            }
        }
        return items;
    }

    private static ParsedFeedItem? ParseItem(XElement element, EventSourceFeedConfig source)
    {
        var title = CleanText(element.Elements().FirstOrDefault(child => child.Name.LocalName == "title")?.Value, 180);
        var link = element.Elements().FirstOrDefault(child => child.Name.LocalName == "link")?.Value?.Trim() ?? "";
        var guid = element.Elements().FirstOrDefault(child => child.Name.LocalName == "guid")?.Value?.Trim();
        var description = CleanText(element.Elements().FirstOrDefault(child => child.Name.LocalName is "description" or "encoded")?.Value, 500);
        var dateText = element.Elements().FirstOrDefault(child => child.Name.LocalName == "pubDate")?.Value;
        if (title.Length < 3 || !Uri.TryCreate(link, UriKind.Absolute, out var linkUri) ||
            linkUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(linkUri.UserInfo)) return null;
        var externalId = string.IsNullOrWhiteSpace(guid) ? link : guid;
        if (externalId.Length > 300) return null;
        DateTime? publishedAt = DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)
            ? parsedDate.UtcDateTime
            : null;
        var rawDescription = element.Elements().FirstOrDefault(child => child.Name.LocalName is "description" or "encoded")?.Value ?? "";
        var sourceText = CleanText(rawDescription, 5000);
        var eventDate = ExtractEventDate(title + " " + description, publishedAt);
        var eventTime = ExtractEventTime(sourceText);
        var eventPlace = ExtractEventPlace(rawDescription);
        return new ParsedFeedItem(externalId, linkUri.AbsoluteUri, title, description, eventDate, publishedAt,
            eventTime, eventPlace, source.DefaultCategory);
    }

    private static string ExtractEventTime(string text)
    {
        var match = EventTime.Match(text);
        return match.Success ? $"{int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture):D2}:{match.Groups["minute"].Value}" : "";
    }

    private static string ExtractEventPlace(string text)
    {
        var decoded = WebUtility.HtmlDecode(text);
        var withParagraphBreaks = HtmlBlockTags.Replace(decoded, "\n");
        var plainText = HtmlTags.Replace(withParagraphBreaks, " ");
        var match = ExplicitEventPlace.Match(plainText);
        if (!match.Success) return "";

        var place = CleanText(match.Groups["place"].Value, 240);
        foreach (var boundary in new[] { "уважаемые", "дорогие", "приглашаем", "приходите", "ждём", "welcome", "dear", "join" })
        {
            var boundaryIndex = place.IndexOf(boundary, StringComparison.OrdinalIgnoreCase);
            if (boundaryIndex > 0) place = place[..boundaryIndex].Trim();
        }
        return place.Length <= 120 ? place : place[..120].Trim();
    }

    private static string TryParseEventTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return TimeOnly.TryParseExact(value, ["HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "";
    }

    private static string MapOperaCategory(string? repertoire, string title)
    {
        var value = (repertoire ?? "").Trim();
        if (value.Contains("опера", StringComparison.OrdinalIgnoreCase)) return "опера";
        if (value.Contains("балет", StringComparison.OrdinalIgnoreCase)) return "балет";
        if (value.Contains("концерт", StringComparison.OrdinalIgnoreCase)) return "концерт";

        if (title.Contains("балет", StringComparison.OrdinalIgnoreCase) || title.Contains("томирис", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("щелкунчик", StringComparison.OrdinalIgnoreCase)) return "балет";
        if (title.Contains("опера", StringComparison.OrdinalIgnoreCase) || title.Contains("турандот", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("алеко", StringComparison.OrdinalIgnoreCase) || title.Contains("тайный брак", StringComparison.OrdinalIgnoreCase)) return "опера";
        if (title.Contains("concert", StringComparison.OrdinalIgnoreCase) || title.Contains("концерт", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("музык", StringComparison.OrdinalIgnoreCase) || title.Contains("flute", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("paganini", StringComparison.OrdinalIgnoreCase) || title.Contains("паганини", StringComparison.OrdinalIgnoreCase)) return "концерт";
        return "спектакль";
    }

    private static DateOnly? ExtractEventDate(string text, DateTime? publishedAtUtc)
    {
        var match = RussianEventDate.Match(text);
        var months = RussianMonths;
        if (!match.Success)
        {
            match = EnglishEventDate.Match(text);
            months = EnglishMonths;
        }
        if (!match.Success || !int.TryParse(match.Groups["day"].Value, CultureInfo.InvariantCulture, out var day)) return null;
        var monthName = match.Groups["month"].Value.ToLowerInvariant();
        if (!months.TryGetValue(monthName, out var month)) return null;
        var publicationDateAstana = publishedAtUtc is { } publishedAt
            ? DateOnly.FromDateTime(publishedAt.AddHours(5))
            : (DateOnly?)null;
        var hasExplicitYear = match.Groups["year"].Success && int.TryParse(match.Groups["year"].Value, CultureInfo.InvariantCulture, out _);
        if (!hasExplicitYear && publicationDateAstana is null) return null;
        var year = hasExplicitYear
            ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)
            : publicationDateAstana!.Value.Year;
        try
        {
            var eventDate = new DateOnly(year, month, day);
            if (!hasExplicitYear && eventDate <= publicationDateAstana!.Value) return null;
            return eventDate;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private bool IsAllowedFeed(EventSourceFeedConfig source)
    {
        if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name) ||
            string.IsNullOrWhiteSpace(source.HobbyId) || !Uri.TryCreate(source.FeedUrl, UriKind.Absolute, out var uri)) return false;
        var allowedHosts = source.AllowedHosts ?? [];
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
               allowedHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MentionsAstana(string text)
        => text.Contains("Astana", StringComparison.OrdinalIgnoreCase) || text.Contains("Астана", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeEventNotice(string text, string[]? configuredKeywords)
        => (configuredKeywords is { Length: > 0 } ? configuredKeywords : DefaultEventKeywords)
            .Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decoded = WebUtility.HtmlDecode(value);
        var plainText = HtmlTags.Replace(decoded, " ");
        var normalized = string.Join(' ', plainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    public sealed class EventSourceFeedConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FeedUrl { get; set; } = "";
        public string HobbyId { get; set; } = "";
        public string[]? AllowedHosts { get; set; }
        public string[]? EventKeywords { get; set; }
        public string Format { get; set; } = "rss";
        public string ItemUrlTemplate { get; set; } = "";
        public int PageCount { get; set; } = 1;
        public string DefaultCategory { get; set; } = "";
        public bool RequireAstanaMention { get; set; } = true;
        public bool RequireEventKeywords { get; set; } = true;
        public bool RequireFutureEventDate { get; set; }
    }

    private sealed record ParsedFeedItem(
        string ExternalId,
        string SourceUrl,
        string Title,
        string Summary,
        DateOnly? EventDate,
        DateTime? PublishedAtUtc,
        string EventTime = "",
        string EventPlace = "",
        string SuggestedCategory = "");

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public LimitedReadStream(Stream inner, long limit) { _inner = inner; _limit = limit; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Count(await _inner.ReadAsync(buffer, offset, count, cancellationToken));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await _inner.ReadAsync(buffer, cancellationToken));
        private int Count(int count)
        {
            _read += count;
            if (_read > _limit) throw new InvalidDataException("RSS feed exceeds the configured size limit.");
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
