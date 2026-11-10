using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/admin/interest-communities")]
public sealed class AdminInterestCommunitiesController(
    TusaMapDbContext db,
    ITelegramAuthService auth,
    IUserStore users,
    IInterestGroupCatalog catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InterestCommunityReviewResponse>>> GetPending(
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var admin = auth.ValidateInitData(initData);
        if (admin is null || !users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);

        var pending = await db.InterestCommunities.AsNoTracking()
            .Where(item => item.Status == "pending")
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var ids = pending.Select(item => item.Id).ToArray();
        var links = ids.Length == 0
            ? []
            : await db.InterestCommunityInterests.AsNoTracking()
                .Where(item => ids.Contains(item.CommunityId))
                .ToListAsync(cancellationToken);

        return Ok(pending.Select(item => new InterestCommunityReviewResponse(
            item.Id, item.Title, item.Description, item.Platform, item.Url, item.CreatedAt,
            links.Where(link => link.CommunityId == item.Id)
                .Select(link => catalog.Find(link.InterestId)?.Name ?? link.InterestId)
                .ToArray())).ToArray());
    }

    [HttpPost("{communityId}/review")]
    public async Task<IActionResult> Review(string communityId, [FromBody] ReviewInterestCommunityRequest request,
        [FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var admin = auth.ValidateInitData(initData);
        if (admin is null || !users.IsAdmin(admin.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        if (request.Action is not ("approve" or "reject")) return BadRequest("Action must be approve or reject.");

        var community = await db.InterestCommunities.FirstOrDefaultAsync(
            item => item.Id == communityId && item.Status == "pending", cancellationToken);
        if (community is null) return NotFound();
        community.Status = request.Action == "approve" ? "approved" : "rejected";
        community.ReviewedAt = DateTime.UtcNow;
        community.ReviewedByTelegramUserId = admin.Id;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}

public sealed record InterestCommunityReviewResponse(
    string Id, string Title, string Description, string Platform, string Url, DateTime CreatedAt, IReadOnlyList<string> Interests);

public sealed record ReviewInterestCommunityRequest(string Action);