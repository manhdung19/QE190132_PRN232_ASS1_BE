using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;
using TaskTrack.Service.Services;

namespace TaskTrack.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/accounts")]
public sealed class AccountsController(IAccountService accounts) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AccountResponse>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await accounts.GetAllAsync(cancellationToken));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AccountResponse>> GetById(int id, CancellationToken cancellationToken) =>
        Ok(await accounts.GetByIdAsync(id, cancellationToken));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<AccountResponse>> Update(int id, [FromBody] AccountUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.FullNameSpecified && request.FullName is null)
            throw ServiceException.Invalid("fullName", "Full name cannot be null.");
        if (request.RoleSpecified && request.Role is null)
            throw ServiceException.Invalid("role", "Role cannot be null.");
        return Ok(await accounts.UpdateAsync(id, request.FullName, request.Role, cancellationToken));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirst("accountId")?.Value, out var actingId) || actingId <= 0)
            return Unauthorized();
        await accounts.DeleteAsync(id, actingId, cancellationToken);
        return NoContent();
    }
}
