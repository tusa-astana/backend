using Microsoft.AspNetCore.Mvc;
using TusaMap.Api.Services;

namespace TusaMap.Api.Controllers;

[ApiController]
[Route("api/admin/work-board")]
public sealed class AdminWorkBoardController : ControllerBase
{
    private const string BoardUrl = "https://raw.githubusercontent.com/yessken/yessken.github.io/main/docs/WORK_BOARD.md";
    private const int MaxBoardLength = 128 * 1024;
    private readonly ITelegramAuthService _auth;
    private readonly IUserStore _users;
    private readonly IHttpClientFactory _clients;

    public AdminWorkBoardController(ITelegramAuthService auth, IUserStore users, IHttpClientFactory clients)
    {
        _auth = auth;
        _users = users;
        _clients = clients;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromHeader(Name = "X-Telegram-Init-Data")] string? initData, CancellationToken cancellationToken)
    {
        var user = _auth.ValidateInitData(initData);
        if (user is null || !_users.IsAdmin(user.Id)) return StatusCode(StatusCodes.Status403Forbidden);

        try
        {
            using var client = _clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var response = await client.GetAsync(BoardUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (response.Content.Headers.ContentLength > MaxBoardLength) return StatusCode(StatusCodes.Status502BadGateway);

            var markdown = await response.Content.ReadAsStringAsync(cancellationToken);
            if (markdown.Length > MaxBoardLength) return StatusCode(StatusCodes.Status502BadGateway);
            return Content(markdown, "text/markdown; charset=utf-8");
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
