using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
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
    private static readonly Regex RussianEventDate = new(
        @"(?<!\d)(?<day>\d{1,2})(?:\s*[–—-]\s*\d{1,2})?\s+(?<month>января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря)(?:\s+(?<year>20\d{2}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EnglishEventDate = new(
        @"\b(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<day>\d{1,2})(?:\s+(?:to|[-–—])\s*\d{1,2})?(?:,?\s*(?<year>20\d{2}))?\b",
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
                    .Where(item => LooksLikeEventNotice(item.Title + " " + item.Summary, source.EventKeywords))
                    .Where(item => !source.RequireFutureEventDate || item.EventDate is { } eventDate && eventDate >= todayAstana)
                    .DistinctBy(item => item.ExternalId, StringComparer.Ordinal)
                    .Take(MaxItemsPerFeed)
                    .ToArray();
                if (eligible.Length == 0) continue;

                var sourceIds = eligible.Select(item => item.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
                var knownIds = (await db.EventSourceItems.AsNoTracking()
                    .Where(item => item.SourceId == source.Id && sourceIds.Contains(item.ExternalId))
                    .Select(item => item.ExternalId)
                    .ToListAsync(cancellationToken))
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var item in eligible.Where(item => !knownIds.Contains(item.ExternalId)))
                {
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
            .Select(ParseItem)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }

    private static ParsedFeedItem? ParseItem(XElement element)
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
        var eventDate = ExtractEventDate(title + " " + description, publishedAt);
        return new ParsedFeedItem(externalId, linkUri.AbsoluteUri, title, description, eventDate, publishedAt);
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
        public bool RequireAstanaMention { get; set; } = true;
        public bool RequireFutureEventDate { get; set; }
    }

    private sealed record ParsedFeedItem(string ExternalId, string SourceUrl, string Title, string Summary, DateOnly? EventDate, DateTime? PublishedAtUtc);

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
