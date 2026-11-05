namespace TusaMap.Api.Models;

public sealed class MeetupAttendance
{
    public string VenueId { get; set; } = "";
    public string MeetupDate { get; set; } = "";
    public long TelegramUserId { get; set; }
    public string ArrivalTime { get; set; } = "";
    public bool IsAnonymous { get; set; } = true;
    public bool OpenToMeet { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
