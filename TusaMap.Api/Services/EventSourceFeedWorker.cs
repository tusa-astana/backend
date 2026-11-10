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
                var eligible = items
                    .Where(item => item.PublishedAtUtc is null || item.PublishedAtUtc >= recentSince)
                    .Where(item => Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var itemUri) &&
                                   allowedHosts.Any(host => string.Equals(itemUri.Host, host, StringComparison.OrdinalIgnoreCase)))
                    .Where(item => MentionsAstana(item.Title + " " + item.Summary))
                    .Where(item => LooksLikeEventNotice(item.Title + " " + item.Summary))
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
        return new ParsedFeedItem(externalId, linkUri.AbsoluteUri, title, description, publishedAt);
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

    private static bool LooksLikeEventNotice(string text)
    {
        string[] keywords = ["championship", "competition", "tournament", "cup", "schedule", "чемпионат", "соревн", "турнир", "кубок", "расписани"];
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

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
    }

    private sealed record ParsedFeedItem(string ExternalId, string SourceUrl, string Title, string Summary, DateTime? PublishedAtUtc);

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
