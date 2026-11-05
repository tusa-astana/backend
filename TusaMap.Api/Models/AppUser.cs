namespace TusaMap.Api.Models;

public class AppUser
{
    public long TelegramUserId { get; set; }
    public string FirstName { get; set; } = "";
    public string? LastName { get; set; }
    public string? Username { get; set; }
    public string? PhotoUrl { get; set; }
    public string Role { get; set; } = "user";
    public bool IsSuspended { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}