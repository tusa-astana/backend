using System.Security.Cryptography;
using TusaMap.Api.Models;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;

namespace TusaMap.Api.Services;

public interface ITicketsStore
{
    IReadOnlyList<Ticket> GetByUserId(long telegramUserId);
    Ticket Add(Ticket t, long telegramUserId);
    Ticket? GetByPaymentReference(string reference);
    Ticket? MarkPaid(string reference, string? telegramPaymentChargeId = null, string? providerPaymentChargeId = null);
}

public class TicketsStore : ITicketsStore
{
    private readonly TusaMapDbContext _db;

    public TicketsStore(TusaMapDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<Ticket> GetByUserId(long telegramUserId)
        => _db.Tickets.AsNoTracking().Where(x => x.TelegramUserId == telegramUserId).ToList();

    public Ticket Add(Ticket t, long telegramUserId)
    {
        t.Id = Guid.NewGuid().ToString("N");
        t.TelegramUserId = telegramUserId;
        t.PurchasedAt = DateTime.UtcNow.ToString("O");
        _db.Tickets.Add(t);
        _db.SaveChanges();
        return t;
    }

    public Ticket? GetByPaymentReference(string reference) => _db.Tickets.FirstOrDefault(x => x.PaymentReference == reference);

    public Ticket? MarkPaid(string reference, string? telegramPaymentChargeId = null, string? providerPaymentChargeId = null)
    {
        var ticket = GetByPaymentReference(reference);
        if (ticket is null) return null;
        if (ticket.PaymentStatus == "paid") return ticket;
        ticket.PaymentStatus = "paid";
        ticket.TelegramPaymentChargeId = telegramPaymentChargeId;
        ticket.ProviderPaymentChargeId = providerPaymentChargeId;
        ticket.QrCode ??= "TUSA-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _db.SaveChanges();
        return ticket;
    }
}
