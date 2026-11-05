namespace TusaMap.Api.Models;

public sealed class InterestGroupMembership
{
    public string InterestId { get; set; } = "";
    public long TelegramUserId { get; set; }
    public bool IsAnonymous { get; set; } = true;
    public bool OpenToMeet { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
