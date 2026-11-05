namespace TusaMap.Api.Models;

public sealed class InterestUserReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InterestId { get; set; } = "";
    public long ReporterTelegramUserId { get; set; }
    public long TargetTelegramUserId { get; set; }
    public string Reason { get; set; } = "";
    public string Details { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public long? ReviewedByTelegramUserId { get; set; }
    public string? ReviewAction { get; set; }
}
