using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;
using TaskTrack.Repo.Repositories;
using TaskTrack.Service.Mapping;

namespace TaskTrack.Service.Services;

public interface IAuthService
{
    Task<AccountResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<AccountResponse?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
}

public sealed class AuthService(IAccountService accounts, IAccountRepository repository) : IAuthService
{
    public async Task<AccountResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        request.FullName = request.FullName?.Trim() ?? string.Empty;
        request.Email = request.Email?.Trim() ?? string.Empty;
        RequestValidation.Validate(request);
        var hash = PasswordHasher.Hash(request.Password);
        return await accounts.CreateStaffAsync(request.FullName, request.Email, hash, cancellationToken);
    }

    public async Task<AccountResponse?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        request.Email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        RequestValidation.Validate(request);
        var account = await repository.FindByEmailAsync(request.Email, cancellationToken);
        return account is not null && PasswordHasher.Verify(request.Password, account.PasswordHash)
            ? account.ToResponse() : null;
    }
}
