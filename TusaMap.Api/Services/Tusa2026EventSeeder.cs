using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;

namespace TusaMap.Api.Services;

public static class Tusa2026EventSeeder
{
    public const string EventId = "tusa-2026";

    public static void Ensure(TusaMapDbContext db, IConfiguration configuration)
    {
        if (!configuration.GetValue("Events:Tusa2026:Enabled", true)) return;

        var date = configuration["Events:Tusa2026:Date"] ?? "2026-10-02";
        var time = configuration["Events:Tusa2026:Time"] ?? "19:00";
        if (!DateTimeOffset.TryParse($"{date}T{time}:00+05:00", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startAt))
            throw new InvalidOperationException("Events:Tusa2026:Date/Time must be a valid Astana local date/time.");

        var ev = db.Events.Include(x => x.TicketCategories).FirstOrDefault(x => x.Id == EventId);
        if (ev is null)
        {
            ev = new EventItem { Id = EventId };
            db.Events.Add(ev);
        }

        ev.Title = "TUSA 2026";
        ev.Description = "Вечер TUSA в Астане. Точный адрес будет отправлен в Telegram участникам с оплаченными билетами за 24 часа до начала.";
        ev.Date = startAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ev.Time = startAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        ev.Place = "Астана · адрес для участников за 24 часа";
        ev.Address = "";
        ev.PrivateAddress = configuration["Events:Tusa2026:PrivateAddress"] ?? "";
        ev.AddressIsPrivate = true;
        ev.AddressRevealAt = startAt.AddHours(-24).UtcDateTime;
        ev.Lat = 51.1694;
        ev.Lng = 71.4494;
        ev.Category = "вечеринка";
        // Offline admission is outside TUSA's Stars-only product policy.
        ev.Price = null;
        ev.ImageUrl = configuration["Events:Tusa2026:ImageUrl"] ?? "https://images.unsplash.com/photo-1470229722913-7c0e2dbbafd3?auto=format&fit=crop&w=1200&q=85";
        ev.OrganizerName = "TUSA";
        ev.OrganizerTelegramId = 0;
        ev.Status = "approved";
        ev.IsDemo = false;

        db.SaveChanges();
    }
}
