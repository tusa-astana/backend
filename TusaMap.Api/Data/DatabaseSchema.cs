using Microsoft.EntityFrameworkCore;
using System.Data;

namespace TusaMap.Api.Data;

public static class DatabaseSchema
{
    public static void EnsureCompatible(TusaMapDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            EnsurePostgresCompatible(db);
            return;
        }
        if (!db.Database.IsSqlite()) return;
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();

        CreateTable(db, "TicketCategories", """
            CREATE TABLE IF NOT EXISTS TicketCategories (
                Id TEXT NOT NULL PRIMARY KEY, EventId TEXT NOT NULL, Name TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '', Price TEXT NOT NULL DEFAULT '0',
                Capacity INTEGER NOT NULL DEFAULT 0, Sold INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """);
        CreateTable(db, "PromoCodes", """
            CREATE TABLE IF NOT EXISTS PromoCodes (
                Id TEXT NOT NULL PRIMARY KEY, Code TEXT NOT NULL, EventId TEXT NULL,
                TicketCategoryId TEXT NULL, DiscountType TEXT NOT NULL, Value TEXT NOT NULL DEFAULT '0',
                MaxUses INTEGER NULL, UsedCount INTEGER NOT NULL DEFAULT 0, PerUserLimit INTEGER NOT NULL DEFAULT 1,
                StartsAt TEXT NULL, ExpiresAt TEXT NULL, IsActive INTEGER NOT NULL DEFAULT 1
            )
            """);
        CreateTable(db, "PromoRedemptions", """
            CREATE TABLE IF NOT EXISTS PromoRedemptions (
                Id TEXT NOT NULL PRIMARY KEY, PromoCodeId TEXT NOT NULL, TelegramUserId INTEGER NOT NULL,
                TicketId TEXT NOT NULL, CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """);
        CreateTable(db, "FunnelEvents", """
            CREATE TABLE IF NOT EXISTS FunnelEvents (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL,
                EventId TEXT NULL, Ref TEXT NULL, TelegramUserId INTEGER NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """);
        CreateTable(db, "AdminDailyReportRuns", """
            CREATE TABLE IF NOT EXISTS AdminDailyReportRuns (
                ReportDate TEXT NOT NULL PRIMARY KEY, SentAtUtc TEXT NOT NULL
            )
            """);
        CreateTable(db, "EventInterests", """
            CREATE TABLE IF NOT EXISTS EventInterests (
                EventId TEXT NOT NULL, TelegramUserId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (EventId, TelegramUserId)
            )
            """);
        CreateTable(db, "EventParticipations", """
            CREATE TABLE IF NOT EXISTS EventParticipations (
                EventId TEXT NOT NULL, TelegramUserId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (EventId, TelegramUserId)
            )
            """);
        CreateTable(db, "MeetupAttendances", """
            CREATE TABLE IF NOT EXISTS MeetupAttendances (
                VenueId TEXT NOT NULL, MeetupDate TEXT NOT NULL, TelegramUserId INTEGER NOT NULL,
                ArrivalTime TEXT NOT NULL DEFAULT '', IsAnonymous INTEGER NOT NULL DEFAULT 1,
                OpenToMeet INTEGER NOT NULL DEFAULT 0, UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (VenueId, MeetupDate, TelegramUserId)
            )
            """);
        CreateTable(db, "InterestGroupMemberships", """
            CREATE TABLE IF NOT EXISTS InterestGroupMemberships (
                InterestId TEXT NOT NULL, TelegramUserId INTEGER NOT NULL,
                IsAnonymous INTEGER NOT NULL DEFAULT 1, OpenToMeet INTEGER NOT NULL DEFAULT 0,
                NotifyNewGatherings INTEGER NOT NULL DEFAULT 0,
                JoinedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (InterestId, TelegramUserId)
            )
            """);
        CreateTable(db, "InterestUserReports", """
            CREATE TABLE IF NOT EXISTS InterestUserReports (
                Id TEXT NOT NULL PRIMARY KEY, InterestId TEXT NOT NULL,
                ReporterTelegramUserId INTEGER NOT NULL, TargetTelegramUserId INTEGER NOT NULL,
                Reason TEXT NOT NULL, Details TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL DEFAULT 'pending',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, ReviewedAt TEXT NULL,
                ReviewedByTelegramUserId INTEGER NULL, ReviewAction TEXT NULL
            )
            """);
        CreateTable(db, "InterestGatherings", """
            CREATE TABLE IF NOT EXISTS InterestGatherings (
                Id TEXT NOT NULL PRIMARY KEY, InterestId TEXT NOT NULL, HostTelegramUserId INTEGER NOT NULL,
                Title TEXT NOT NULL, MeetupDate TEXT NOT NULL, StartTime TEXT NOT NULL,
                Place TEXT NOT NULL, Details TEXT NOT NULL DEFAULT '', IsOpen INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, IsCancelled INTEGER NOT NULL DEFAULT 0
            )
            """);
        CreateTable(db, "InterestGatheringAttendances", """
            CREATE TABLE IF NOT EXISTS InterestGatheringAttendances (
                GatheringId TEXT NOT NULL, TelegramUserId INTEGER NOT NULL,
                JoinedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (GatheringId, TelegramUserId)
            )
            """);
        CreateTable(db, "InterestGatheringNotifications", """
            CREATE TABLE IF NOT EXISTS InterestGatheringNotifications (
                GatheringId TEXT NOT NULL, TelegramUserId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, ReadAt TEXT NULL,
                PRIMARY KEY (GatheringId, TelegramUserId)
            )
            """);
        CreateTable(db, "InterestCommunities", """
            CREATE TABLE IF NOT EXISTS InterestCommunities (
                Id TEXT NOT NULL PRIMARY KEY, Title TEXT NOT NULL, Description TEXT NOT NULL DEFAULT '',
                Platform TEXT NOT NULL, Url TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'pending',
                CreatedAt TEXT NOT NULL, ReviewedAt TEXT NULL, ReviewedByTelegramUserId INTEGER NULL
            )
            """);
        CreateTable(db, "InterestCommunityInterests", """
            CREATE TABLE IF NOT EXISTS InterestCommunityInterests (
                CommunityId TEXT NOT NULL, InterestId TEXT NOT NULL,
                PRIMARY KEY (CommunityId, InterestId)
            )
            """);
        CreateTable(db, "OrganizerSubscriptions", """
            CREATE TABLE IF NOT EXISTS OrganizerSubscriptions (
                TelegramUserId INTEGER NOT NULL PRIMARY KEY, Plan TEXT NOT NULL DEFAULT 'starter',
                Status TEXT NOT NULL DEFAULT 'inactive', ExpiresAt TEXT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                LastTelegramChargeId TEXT NULL, TermsAcceptedAt TEXT NULL, TermsVersion TEXT NULL
            )
            """);
        CreateTable(db, "EventPromotions", """
            CREATE TABLE IF NOT EXISTS EventPromotions (
                Id TEXT NOT NULL PRIMARY KEY, EventId TEXT NOT NULL UNIQUE,
                OrganizerTelegramId INTEGER NOT NULL, StarsAmount INTEGER NOT NULL,
                Status TEXT NOT NULL DEFAULT 'pending', TermsAcceptedAt TEXT NOT NULL,
                TermsVersion TEXT NOT NULL, TelegramPaymentChargeId TEXT NULL UNIQUE,
                InvoiceNonce TEXT NULL,
                CreatedAt TEXT NOT NULL, ActivatedAt TEXT NULL, ExpiresAt TEXT NULL
            )
            """);
        CreateTable(db, "TicketCheckIns", """
            CREATE TABLE IF NOT EXISTS TicketCheckIns (
                Id TEXT NOT NULL PRIMARY KEY, TicketId TEXT NOT NULL,
                CheckedByTelegramUserId INTEGER NOT NULL, CheckedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """);
        CreateTable(db, "BotMessageLogs", """
            CREATE TABLE IF NOT EXISTS BotMessageLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, UpdateId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL, ChatId INTEGER NOT NULL, TelegramUserId INTEGER NOT NULL,
                SenderName TEXT NOT NULL DEFAULT '', Username TEXT NULL, MessageType TEXT NOT NULL DEFAULT 'text',
                Content TEXT NOT NULL DEFAULT '', ReceivedAt TEXT NOT NULL
            )
            """);
        CreateTable(db, "EventSourceItems", """
            CREATE TABLE IF NOT EXISTS EventSourceItems (
                Id TEXT NOT NULL PRIMARY KEY, SourceId TEXT NOT NULL, SourceName TEXT NOT NULL,
                ExternalId TEXT NOT NULL, SourceUrl TEXT NOT NULL, Title TEXT NOT NULL,
                Summary TEXT NOT NULL DEFAULT '', HobbyId TEXT NOT NULL, EventDate TEXT NULL, PublishedAtUtc TEXT NULL,
                ImportedAtUtc TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'pending', EventId TEXT NULL
            )
            """);

        AddColumnIfMissing(db, "EventSourceItems", "EventDate", "TEXT NULL");
        AddColumnIfMissing(db, "Events", "OrganizerTelegramId", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "Events", "OrganizerEmail", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "OrganizerPhone", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "Status", "TEXT NOT NULL DEFAULT 'approved'");
        AddColumnIfMissing(db, "Events", "CreatedAt", "TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP");
        AddColumnIfMissing(db, "Events", "AddressIsPrivate", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "Events", "AddressRevealAt", "TEXT NULL");
        AddColumnIfMissing(db, "Events", "PrivateAddress", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "IsDemo", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "Events", "IsPublicEvent", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "Events", "TelegramSourceUrl", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "TicketUrl", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "TicketSellerName", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "FeaturedUntilUtc", "TEXT NULL");
        AddColumnIfMissing(db, "Events", "HobbyId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "SourceUrl", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Events", "SourceVerifiedAtUtc", "TEXT NULL");
        AddColumnIfMissing(db, "EventPromotions", "InvoiceNonce", "TEXT NULL");
        AddColumnIfMissing(db, "Users", "PhotoUrl", "TEXT NULL");
        AddColumnIfMissing(db, "Users", "IsSuspended", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "Users", "SuspendedAt", "TEXT NULL");
        AddColumnIfMissing(db, "InterestGroupMemberships", "NotifyNewGatherings", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "InterestGatherings", "IsOpen", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "Tickets", "PaymentMethod", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Tickets", "PaymentStatus", "TEXT NOT NULL DEFAULT 'pending'");
        AddColumnIfMissing(db, "Tickets", "TelegramUserId", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "Tickets", "PaymentReference", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "TelegramPaymentChargeId", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "ProviderPaymentChargeId", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "PrivateAddressSentAt", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "TermsAcceptedAt", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "TermsVersion", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "PaymentCheckoutAt", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "TicketCategoryId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Tickets", "TicketCategoryName", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "Tickets", "Quantity", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "Tickets", "BaseAmount", "TEXT NOT NULL DEFAULT '0'");
        AddColumnIfMissing(db, "Tickets", "DiscountAmount", "TEXT NOT NULL DEFAULT '0'");
        AddColumnIfMissing(db, "Tickets", "CommissionAmount", "TEXT NOT NULL DEFAULT '0'");
        AddColumnIfMissing(db, "Tickets", "TotalAmount", "TEXT NOT NULL DEFAULT '0'");
        AddColumnIfMissing(db, "Tickets", "PromoCode", "TEXT NULL");
        AddColumnIfMissing(db, "Tickets", "RefundStatus", "TEXT NOT NULL DEFAULT 'none'");
        AddColumnIfMissing(db, "Tickets", "CancelledAt", "TEXT NULL");
        AddColumnIfMissing(db, "FunnelEvents", "VisitorId", "TEXT NULL");
        AddColumnIfMissing(db, "OrganizerSubscriptions", "LastTelegramChargeId", "TEXT NULL");
        AddColumnIfMissing(db, "OrganizerSubscriptions", "TermsAcceptedAt", "TEXT NULL");
        AddColumnIfMissing(db, "OrganizerSubscriptions", "TermsVersion", "TEXT NULL");
        AddColumnIfMissing(db, "EventInterests", "TicketAvailabilityNotifiedAt", "TEXT NULL");

        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_PromoCodes_Code ON PromoCodes (Code)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_EventPromotions_EventId ON EventPromotions (EventId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_EventPromotions_TelegramPaymentChargeId ON EventPromotions (TelegramPaymentChargeId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_PromoRedemptions_Code_User ON PromoRedemptions (PromoCodeId, TelegramUserId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_FunnelEvents_Name_Event_Ref ON FunnelEvents (Name, EventId, Ref)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_FunnelEvents_CreatedAt ON FunnelEvents (CreatedAt)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_TicketCheckIns_TicketId ON TicketCheckIns (TicketId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_BotMessageLogs_UpdateId ON BotMessageLogs (UpdateId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_EventSourceItems_SourceId_ExternalId ON EventSourceItems (SourceId, ExternalId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_EventSourceItems_Status_ImportedAtUtc ON EventSourceItems (Status, ImportedAtUtc)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Events_HobbyId ON Events (HobbyId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_EventInterests_EventId ON EventInterests (EventId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_EventParticipations_EventId ON EventParticipations (EventId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MeetupAttendances_MeetupDate_VenueId ON MeetupAttendances (MeetupDate, VenueId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InterestGroupMemberships_TelegramUserId ON InterestGroupMemberships (TelegramUserId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_InterestUserReports_Interest_Reporter_Target ON InterestUserReports (InterestId, ReporterTelegramUserId, TargetTelegramUserId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InterestUserReports_Status ON InterestUserReports (Status)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InterestGatherings_Interest_Date_Cancelled ON InterestGatherings (InterestId, MeetupDate, IsCancelled)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InterestGatheringAttendances_TelegramUserId ON InterestGatheringAttendances (TelegramUserId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InterestGatheringNotifications_TelegramUserId_ReadAt ON InterestGatheringNotifications (TelegramUserId, ReadAt)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InterestCommunities_Status ON InterestCommunities (Status)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_InterestCommunities_Url ON InterestCommunities (Url)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InterestCommunityInterests_InterestId ON InterestCommunityInterests (InterestId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Tickets_TelegramPaymentChargeId ON Tickets (TelegramPaymentChargeId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Tickets_ProviderPaymentChargeId ON Tickets (ProviderPaymentChargeId)");
        db.Database.ExecuteSqlRaw("UPDATE Events SET IsDemo = 1 WHERE Id IN ('1','2','3','4') AND OrganizerTelegramId = 0");
    }

    private static void EnsurePostgresCompatible(TusaMapDbContext db)
    {
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"AdminDailyReportRuns\" (\"ReportDate\" TEXT PRIMARY KEY, \"SentAtUtc\" TIMESTAMPTZ NOT NULL)");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"TicketUrl\" TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"TicketSellerName\" TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"FeaturedUntilUtc\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"HobbyId\" TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"SourceUrl\" TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"SourceVerifiedAtUtc\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_Events_HobbyId\" ON \"Events\" (\"HobbyId\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"EventPromotions\" (\"Id\" TEXT PRIMARY KEY, \"EventId\" TEXT NOT NULL UNIQUE, \"OrganizerTelegramId\" BIGINT NOT NULL, \"StarsAmount\" INTEGER NOT NULL, \"Status\" TEXT NOT NULL DEFAULT 'pending', \"TermsAcceptedAt\" TIMESTAMPTZ NOT NULL, \"TermsVersion\" TEXT NOT NULL, \"TelegramPaymentChargeId\" TEXT NULL UNIQUE, \"InvoiceNonce\" TEXT NULL, \"CreatedAt\" TIMESTAMPTZ NOT NULL, \"ActivatedAt\" TIMESTAMPTZ NULL, \"ExpiresAt\" TIMESTAMPTZ NULL)");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"EventPromotions\" ADD COLUMN IF NOT EXISTS \"InvoiceNonce\" TEXT NULL");
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "OrganizerSubscriptions" (
                "TelegramUserId" BIGINT PRIMARY KEY,
                "Plan" TEXT NOT NULL DEFAULT 'starter',
                "Status" TEXT NOT NULL DEFAULT 'inactive',
                "ExpiresAt" TIMESTAMPTZ NULL,
                "UpdatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "LastTelegramChargeId" TEXT NULL,
                "TermsAcceptedAt" TIMESTAMPTZ NULL,
                "TermsVersion" TEXT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "BotMessageLogs" (
                "Id" BIGSERIAL PRIMARY KEY, "UpdateId" BIGINT NOT NULL,
                "MessageId" BIGINT NOT NULL, "ChatId" BIGINT NOT NULL, "TelegramUserId" BIGINT NOT NULL,
                "SenderName" VARCHAR(160) NOT NULL DEFAULT '', "Username" VARCHAR(64) NULL,
                "MessageType" VARCHAR(40) NOT NULL DEFAULT 'text', "Content" VARCHAR(4096) NOT NULL DEFAULT '',
                "ReceivedAt" TIMESTAMPTZ NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_BotMessageLogs_UpdateId\" ON \"BotMessageLogs\" (\"UpdateId\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"EventSourceItems\" (\"Id\" TEXT PRIMARY KEY, \"SourceId\" TEXT NOT NULL, \"SourceName\" TEXT NOT NULL, \"ExternalId\" TEXT NOT NULL, \"SourceUrl\" TEXT NOT NULL, \"Title\" TEXT NOT NULL, \"Summary\" TEXT NOT NULL DEFAULT '', \"HobbyId\" TEXT NOT NULL, \"EventDate\" DATE NULL, \"PublishedAtUtc\" TIMESTAMPTZ NULL, \"ImportedAtUtc\" TIMESTAMPTZ NOT NULL, \"Status\" TEXT NOT NULL DEFAULT 'pending', \"EventId\" TEXT NULL)");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"EventSourceItems\" ADD COLUMN IF NOT EXISTS \"EventDate\" DATE NULL");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_EventSourceItems_SourceId_ExternalId\" ON \"EventSourceItems\" (\"SourceId\", \"ExternalId\")");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_EventSourceItems_Status_ImportedAtUtc\" ON \"EventSourceItems\" (\"Status\", \"ImportedAtUtc\")");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Tickets\" ADD COLUMN IF NOT EXISTS \"TelegramPaymentChargeId\" TEXT NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Tickets\" ADD COLUMN IF NOT EXISTS \"ProviderPaymentChargeId\" TEXT NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Tickets\" ADD COLUMN IF NOT EXISTS \"PrivateAddressSentAt\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Tickets\" ADD COLUMN IF NOT EXISTS \"TermsAcceptedAt\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Tickets\" ADD COLUMN IF NOT EXISTS \"TermsVersion\" TEXT NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Tickets\" ADD COLUMN IF NOT EXISTS \"PaymentCheckoutAt\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"OrganizerSubscriptions\" ADD COLUMN IF NOT EXISTS \"LastTelegramChargeId\" TEXT NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"OrganizerSubscriptions\" ADD COLUMN IF NOT EXISTS \"TermsAcceptedAt\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"OrganizerSubscriptions\" ADD COLUMN IF NOT EXISTS \"TermsVersion\" TEXT NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"AddressIsPrivate\" BOOLEAN NOT NULL DEFAULT FALSE");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"AddressRevealAt\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"PrivateAddress\" TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"IsDemo\" BOOLEAN NOT NULL DEFAULT FALSE");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"IsPublicEvent\" BOOLEAN NOT NULL DEFAULT FALSE");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Events\" ADD COLUMN IF NOT EXISTS \"TelegramSourceUrl\" TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"FunnelEvents\" ADD COLUMN IF NOT EXISTS \"VisitorId\" TEXT NULL");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_FunnelEvents_CreatedAt\" ON \"FunnelEvents\" (\"CreatedAt\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"EventInterests\" (\"EventId\" TEXT NOT NULL, \"TelegramUserId\" BIGINT NOT NULL, \"CreatedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (\"EventId\", \"TelegramUserId\"))");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_EventInterests_EventId\" ON \"EventInterests\" (\"EventId\")");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"EventInterests\" ADD COLUMN IF NOT EXISTS \"TicketAvailabilityNotifiedAt\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_EventInterests_EventId_TicketAvailabilityNotifiedAt\" ON \"EventInterests\" (\"EventId\", \"TicketAvailabilityNotifiedAt\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"EventParticipations\" (\"EventId\" TEXT NOT NULL, \"TelegramUserId\" BIGINT NOT NULL, \"CreatedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (\"EventId\", \"TelegramUserId\"))");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_EventParticipations_EventId\" ON \"EventParticipations\" (\"EventId\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"MeetupAttendances\" (\"VenueId\" TEXT NOT NULL, \"MeetupDate\" TEXT NOT NULL, \"TelegramUserId\" BIGINT NOT NULL, \"ArrivalTime\" TEXT NOT NULL DEFAULT '', \"IsAnonymous\" BOOLEAN NOT NULL DEFAULT TRUE, \"OpenToMeet\" BOOLEAN NOT NULL DEFAULT FALSE, \"UpdatedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (\"VenueId\", \"MeetupDate\", \"TelegramUserId\"))");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_MeetupAttendances_MeetupDate_VenueId\" ON \"MeetupAttendances\" (\"MeetupDate\", \"VenueId\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"InterestGroupMemberships\" (\"InterestId\" TEXT NOT NULL, \"TelegramUserId\" BIGINT NOT NULL, \"IsAnonymous\" BOOLEAN NOT NULL DEFAULT TRUE, \"OpenToMeet\" BOOLEAN NOT NULL DEFAULT FALSE, \"NotifyNewGatherings\" BOOLEAN NOT NULL DEFAULT FALSE, \"JoinedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (\"InterestId\", \"TelegramUserId\"))");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"InterestGroupMemberships\" ADD COLUMN IF NOT EXISTS \"NotifyNewGatherings\" BOOLEAN NOT NULL DEFAULT FALSE");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_InterestGroupMemberships_TelegramUserId\" ON \"InterestGroupMemberships\" (\"TelegramUserId\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"InterestUserReports\" (\"Id\" TEXT PRIMARY KEY, \"InterestId\" TEXT NOT NULL, \"ReporterTelegramUserId\" BIGINT NOT NULL, \"TargetTelegramUserId\" BIGINT NOT NULL, \"Reason\" TEXT NOT NULL, \"Details\" TEXT NOT NULL DEFAULT '', \"Status\" TEXT NOT NULL DEFAULT 'pending', \"CreatedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, \"ReviewedAt\" TIMESTAMPTZ NULL, \"ReviewedByTelegramUserId\" BIGINT NULL, \"ReviewAction\" TEXT NULL)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_InterestUserReports_Interest_Reporter_Target\" ON \"InterestUserReports\" (\"InterestId\", \"ReporterTelegramUserId\", \"TargetTelegramUserId\")");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_InterestUserReports_Status\" ON \"InterestUserReports\" (\"Status\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"InterestGatherings\" (\"Id\" TEXT PRIMARY KEY, \"InterestId\" TEXT NOT NULL, \"HostTelegramUserId\" BIGINT NOT NULL, \"Title\" TEXT NOT NULL, \"MeetupDate\" TEXT NOT NULL, \"StartTime\" TEXT NOT NULL, \"Place\" TEXT NOT NULL, \"Details\" TEXT NOT NULL DEFAULT '', \"IsOpen\" BOOLEAN NOT NULL DEFAULT TRUE, \"CreatedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, \"IsCancelled\" BOOLEAN NOT NULL DEFAULT FALSE)");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"InterestGatherings\" ADD COLUMN IF NOT EXISTS \"IsOpen\" BOOLEAN NOT NULL DEFAULT TRUE");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_InterestGatherings_Interest_Date_Cancelled\" ON \"InterestGatherings\" (\"InterestId\", \"MeetupDate\", \"IsCancelled\")");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"InterestGatheringAttendances\" (\"GatheringId\" TEXT NOT NULL, \"TelegramUserId\" BIGINT NOT NULL, \"JoinedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (\"GatheringId\", \"TelegramUserId\"))");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"InterestGatheringNotifications\" (\"GatheringId\" TEXT NOT NULL, \"TelegramUserId\" BIGINT NOT NULL, \"CreatedAt\" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP, \"ReadAt\" TIMESTAMPTZ NULL, PRIMARY KEY (\"GatheringId\", \"TelegramUserId\"))");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"InterestCommunities\" (\"Id\" TEXT PRIMARY KEY, \"Title\" TEXT NOT NULL, \"Description\" TEXT NOT NULL DEFAULT '', \"Platform\" TEXT NOT NULL, \"Url\" TEXT NOT NULL, \"Status\" TEXT NOT NULL DEFAULT 'pending', \"CreatedAt\" TIMESTAMPTZ NOT NULL, \"ReviewedAt\" TIMESTAMPTZ NULL, \"ReviewedByTelegramUserId\" BIGINT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"InterestCommunityInterests\" (\"CommunityId\" TEXT NOT NULL, \"InterestId\" TEXT NOT NULL, PRIMARY KEY (\"CommunityId\", \"InterestId\"))");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_InterestCommunities_Status\" ON \"InterestCommunities\" (\"Status\")");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_InterestCommunities_Url\" ON \"InterestCommunities\" (\"Url\")");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_InterestCommunityInterests_InterestId\" ON \"InterestCommunityInterests\" (\"InterestId\")");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_InterestGatheringNotifications_TelegramUserId_ReadAt\" ON \"InterestGatheringNotifications\" (\"TelegramUserId\", \"ReadAt\")");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_InterestGatheringAttendances_TelegramUserId\" ON \"InterestGatheringAttendances\" (\"TelegramUserId\")");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"PhotoUrl\" TEXT NULL");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"IsSuspended\" BOOLEAN NOT NULL DEFAULT FALSE");
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"SuspendedAt\" TIMESTAMPTZ NULL");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Tickets_TelegramPaymentChargeId\" ON \"Tickets\" (\"TelegramPaymentChargeId\")");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Tickets_ProviderPaymentChargeId\" ON \"Tickets\" (\"ProviderPaymentChargeId\")");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TicketCheckIns_TicketId\" ON \"TicketCheckIns\" (\"TicketId\")");
        db.Database.ExecuteSqlRaw("UPDATE \"Events\" SET \"IsDemo\" = TRUE WHERE \"Id\" IN ('1','2','3','4') AND \"OrganizerTelegramId\" = 0");
    }

    private static void CreateTable(TusaMapDbContext db, string _, string sql) => db.Database.ExecuteSqlRaw(sql);

    private static void AddColumnIfMissing(TusaMapDbContext db, string table, string column, string definition)
    {
        using (var columns = db.Database.GetDbConnection().CreateCommand())
        {
            columns.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        using var alter = db.Database.GetDbConnection().CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        alter.ExecuteNonQuery();
    }
}
