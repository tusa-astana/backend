using Microsoft.AspNetCore.Mvc;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/events/{eventId}")]
public class TicketCatalogController : ControllerBase
{
    [HttpPost("ticket-categories")]
    public IActionResult AddCategory()
    {
        return StatusCode(StatusCodes.Status410Gone, "TUSA does not manage or sell offline event tickets.");
    }

    [HttpPost("promo-codes")]
    public IActionResult AddPromoCode()
    {
        return StatusCode(StatusCodes.Status410Gone, "TUSA does not manage or sell offline event tickets.");
    }
}
