using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;

namespace TusaMap.Api.Models;

public class EventItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Date { get; set; } = "";
    public string Time { get; set; } = "";
    public string Place { get; set; } = "";
    public string Address { get; set; } = "";
    public bool AddressIsPrivate { get; set; }
    public DateTime? AddressRevealAt { get; set; }
    [JsonIgnore]
    public string PrivateAddress { get; set; } = "";
    public bool IsDemo { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string Category { get; set; } = "";
    public decimal? Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public bool IsPublicEvent { get; set; }
    public string TelegramSourceUrl { get; set; } = "";
    public string TicketUrl { get; set; } = "";
    public string TicketSellerName { get; set; } = "";
    public string OrganizerName { get; set; } = "";
    public string OrganizerEmail { get; set; } = "";
    public string OrganizerPhone { get; set; } = "";
    public long OrganizerTelegramId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FeaturedUntilUtc { get; set; }
    [NotMapped]
    public bool Featured => FeaturedUntilUtc > DateTime.UtcNow;
    public List<TicketCategory> TicketCategories { get; set; } = [];
    [NotMapped]
    public bool TicketSalesEnabled { get; set; }
}
