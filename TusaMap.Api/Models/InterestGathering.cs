namespace TusaMap.Api.Models;

public sealed class InterestGathering
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InterestId { get; set; } = "";
    public long HostTelegramUserId { get; set; }
    public string Title { get; set; } = "";
    public string MeetupDate { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string Place { get; set; } = "";
    public string Details { get; set; } = "";
    public bool IsOpen { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsCancelled { get; set; }
}
