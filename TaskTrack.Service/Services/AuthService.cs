using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;

namespace TaskTrack.Service.Services;

public interface IAuthService
{
    Task<AccountResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
}

public sealed class AuthService(IAccountService accounts) : IAuthService
{
    public async Task<AccountResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        request.FullName = request.FullName?.Trim() ?? string.Empty;
        request.Email = request.Email?.Trim() ?? string.Empty;
        RequestValidation.Validate(request);
        var hash = PasswordHasher.Hash(request.Password);
        return await accounts.CreateStaffAsync(request.FullName, request.Email, hash, cancellationToken);
    }
}
