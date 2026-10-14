using Microsoft.AspNetCore.Mvc;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Services;
using TaskTrack.API.Security;

namespace TaskTrack.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService auth, JwtTokenService tokens) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AccountResponse>> Register(
        [FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var account = await auth.RegisterAsync(request, cancellationToken);
        return Created($"/api/accounts/{account.AccountId}", account);
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var account = await auth.AuthenticateAsync(request, cancellationToken);
        if (account is null) return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid email or password" });
        var issued = tokens.Issue(account);
        return Ok(new LoginResponse(issued.Token, "Bearer", issued.ExpiresAt, account));
    }
}
