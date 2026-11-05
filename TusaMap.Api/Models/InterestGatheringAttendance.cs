namespace TusaMap.Api.Models;

public sealed class InterestGatheringAttendance
{
    public string GatheringId { get; set; } = "";
    public long TelegramUserId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
