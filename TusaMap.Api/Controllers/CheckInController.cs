using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/check-in")]
public class CheckInController : ControllerBase
{
    private readonly TusaMapDbContext _db;
    private readonly ITelegramAuthService _auth;
    private readonly IUserStore _users;

    public CheckInController(TusaMapDbContext db, ITelegramAuthService auth, IUserStore users)
    {
        _db = db;
        _auth = auth;
        _users = users;
    }

    [HttpPost("scan")]
    public IActionResult Scan([FromBody] ScanTicketRequest request, [FromHeader(Name = "X-Telegram-Init-Data")] string? initData)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null) return Unauthorized();
        var operatorUser = _users.Get(user.Id);
        if (operatorUser?.Role is not ("admin" or "checker")) return Forbid();

        var ticket = _db.Tickets.Include(x => x.CheckIn).FirstOrDefault(x => x.QrCode == request.QrCode);
        if (ticket is null) return NotFound(new { message = "Билет не найден" });
        if (ticket.PaymentStatus != "paid") return Conflict(new { message = "Билет не оплачен" });
        if (ticket.CancelledAt is not null || ticket.RefundStatus != "none") return Conflict(new { message = "Билет отменён или возвращён" });
        if (ticket.CheckIn is not null) return Conflict(new { message = "Билет уже использован", checkedAt = ticket.CheckIn.CheckedAt });

        ticket.CheckIn = new Models.TicketCheckIn { TicketId = ticket.Id, CheckedByTelegramUserId = user.Id };
        try
        {
            _db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var existingCheckIn = _db.TicketCheckIns.AsNoTracking().FirstOrDefault(x => x.TicketId == ticket.Id);
            if (existingCheckIn is null) throw;

            return Conflict(new { message = "Билет уже использован", checkedAt = existingCheckIn.CheckedAt });
        }

        return Ok(new { ticket.Id, ticket.EventTitle, ticket.TicketCategoryName, ticket.Quantity, checkedAt = ticket.CheckIn.CheckedAt });
    }
}

public class ScanTicketRequest
{
    [Required, StringLength(120)] public string QrCode { get; set; } = "";
}
