using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Models;

namespace TusaMap.Api.Data;

public class TusaMapDbContext : DbContext
{
    public TusaMapDbContext(DbContextOptions<TusaMapDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<OrganizerSubscription> OrganizerSubscriptions => Set<OrganizerSubscription>();
    public DbSet<EventPromotion> EventPromotions => Set<EventPromotion>();
    public DbSet<EventItem> Events => Set<EventItem>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketCategory> TicketCategories => Set<TicketCategory>();
    public DbSet<PromoCode> PromoCodes => Set<PromoCode>();
    public DbSet<PromoRedemption> PromoRedemptions => Set<PromoRedemption>();
    public DbSet<FunnelEvent> FunnelEvents => Set<FunnelEvent>();
    public DbSet<AdminDailyReportRun> AdminDailyReportRuns => Set<AdminDailyReportRun>();
    public DbSet<EventInterest> EventInterests => Set<EventInterest>();
    public DbSet<EventParticipation> EventParticipations => Set<EventParticipation>();
    public DbSet<MeetupAttendance> MeetupAttendances => Set<MeetupAttendance>();
    public DbSet<InterestGroupMembership> InterestGroupMemberships => Set<InterestGroupMembership>();
    public DbSet<InterestUserReport> InterestUserReports => Set<InterestUserReport>();
    public DbSet<InterestGathering> InterestGatherings => Set<InterestGathering>();
    public DbSet<InterestGatheringAttendance> InterestGatheringAttendances => Set<InterestGatheringAttendance>();
    public DbSet<TicketCheckIn> TicketCheckIns => Set<TicketCheckIn>();
    public DbSet<BotMessageLog> BotMessageLogs => Set<BotMessageLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>().HasKey(x => x.TelegramUserId);
        modelBuilder.Entity<OrganizerSubscription>().HasKey(x => x.TelegramUserId);
        modelBuilder.Entity<EventPromotion>().HasKey(x => x.Id);
        modelBuilder.Entity<EventPromotion>().HasIndex(x => x.EventId).IsUnique();
        modelBuilder.Entity<EventPromotion>().HasIndex(x => x.TelegramPaymentChargeId).IsUnique();
        modelBuilder.Entity<EventItem>().Property(x => x.Price).HasPrecision(12, 2);
        modelBuilder.Entity<Ticket>().HasIndex(x => x.PaymentReference).IsUnique();
        modelBuilder.Entity<Ticket>().HasIndex(x => x.TelegramPaymentChargeId).IsUnique();
        modelBuilder.Entity<Ticket>().HasIndex(x => x.ProviderPaymentChargeId).IsUnique();
        modelBuilder.Entity<Ticket>().HasIndex(x => new { x.TelegramUserId, x.EventId });
        modelBuilder.Entity<TicketCategory>().HasKey(x => x.Id);
        modelBuilder.Entity<EventItem>()
            .HasMany(x => x.TicketCategories)
            .WithOne()
            .HasForeignKey(x => x.EventId)
            .HasPrincipalKey(x => x.Id);
        modelBuilder.Entity<TicketCategory>().Property(x => x.Price).HasPrecision(12, 2);
        modelBuilder.Entity<PromoCode>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<PromoCode>().Property(x => x.Value).HasPrecision(12, 2);
        modelBuilder.Entity<PromoRedemption>().HasIndex(x => new { x.PromoCodeId, x.TelegramUserId });
        modelBuilder.Entity<FunnelEvent>().HasIndex(x => new { x.Name, x.EventId, x.Ref });
        modelBuilder.Entity<FunnelEvent>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<AdminDailyReportRun>().HasKey(x => x.ReportDate);
        modelBuilder.Entity<EventInterest>().HasKey(x => new { x.EventId, x.TelegramUserId });
        modelBuilder.Entity<EventInterest>().HasIndex(x => new { x.EventId, x.TicketAvailabilityNotifiedAt });
        modelBuilder.Entity<EventParticipation>().HasKey(x => new { x.EventId, x.TelegramUserId });
        modelBuilder.Entity<MeetupAttendance>().HasKey(x => new { x.VenueId, x.MeetupDate, x.TelegramUserId });
        modelBuilder.Entity<MeetupAttendance>().HasIndex(x => new { x.MeetupDate, x.VenueId });
        modelBuilder.Entity<InterestGroupMembership>().HasKey(x => new { x.InterestId, x.TelegramUserId });
        modelBuilder.Entity<InterestGroupMembership>().HasIndex(x => x.TelegramUserId);
        modelBuilder.Entity<InterestUserReport>().HasKey(x => x.Id);
        modelBuilder.Entity<InterestUserReport>().HasIndex(x => new { x.InterestId, x.ReporterTelegramUserId, x.TargetTelegramUserId }).IsUnique();
        modelBuilder.Entity<InterestUserReport>().HasIndex(x => x.Status);
        modelBuilder.Entity<InterestGathering>().HasKey(x => x.Id);
        modelBuilder.Entity<InterestGathering>().HasIndex(x => new { x.InterestId, x.MeetupDate, x.IsCancelled });
        modelBuilder.Entity<InterestGatheringAttendance>().HasKey(x => new { x.GatheringId, x.TelegramUserId });
        modelBuilder.Entity<InterestGatheringAttendance>().HasIndex(x => x.TelegramUserId);
        modelBuilder.Entity<TicketCheckIn>().HasKey(x => x.Id);
        modelBuilder.Entity<TicketCheckIn>().HasIndex(x => x.TicketId).IsUnique();
        modelBuilder.Entity<Ticket>().HasOne(x => x.CheckIn).WithOne().HasForeignKey<TicketCheckIn>(x => x.TicketId);
        modelBuilder.Entity<BotMessageLog>().HasIndex(x => x.UpdateId).IsUnique();
        modelBuilder.Entity<BotMessageLog>().Property(x => x.SenderName).HasMaxLength(160);
        modelBuilder.Entity<BotMessageLog>().Property(x => x.Username).HasMaxLength(64);
        modelBuilder.Entity<BotMessageLog>().Property(x => x.MessageType).HasMaxLength(40);
        modelBuilder.Entity<BotMessageLog>().Property(x => x.Content).HasMaxLength(4096);
    }
}