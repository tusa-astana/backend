namespace TusaMap.Api.Models;

public sealed class InterestGatheringNotification
{
    public string GatheringId { get; set; } = "";
    public long TelegramUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}
