namespace TusaMap.Api.Models;

public sealed class EventSourceItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string HobbyId { get; set; } = "";
    public DateOnly? EventDate { get; set; }
    public string EventTime { get; set; } = "";
    public string EventPlace { get; set; } = "";
    public string SuggestedCategory { get; set; } = "";
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "pending";
    public string? EventId { get; set; }
}
