namespace TusaMap.Api.Models;

public class EventPromotion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EventId { get; set; } = "";
    public long OrganizerTelegramId { get; set; }
    public int StarsAmount { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime TermsAcceptedAt { get; set; }
    public string TermsVersion { get; set; } = "2026-11-09";
    public string? TelegramPaymentChargeId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
