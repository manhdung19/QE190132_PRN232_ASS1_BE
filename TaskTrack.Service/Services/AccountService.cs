using SystemAccount = TaskTrack.Repo.Models.SystemAccount;
using TaskTrack.Repo.Repositories;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;
using TaskTrack.Service.Mapping;

namespace TaskTrack.Service.Services;

public interface IAccountService
{
    Task<IReadOnlyList<AccountResponse>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<AccountResponse> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    // Password is hashed by the auth layer before this method is called.
    Task<AccountResponse> CreateStaffAsync(string fullName, string email, string passwordHash, CancellationToken cancellationToken = default);
    Task<AccountResponse> UpdateAsync(int id, string? fullName, short? role, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, int actingAccountId, CancellationToken cancellationToken = default);
}

public sealed class AccountService(IAccountRepository repository, IUnitOfWork unitOfWork) : IAccountService
{
    public async Task<IReadOnlyList<AccountResponse>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetAllAsync(cancellationToken)).Select(x => x.ToResponse()).ToArray();

    public async Task<AccountResponse> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var account = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        return account.ToResponse();
    }

    public async Task<AccountResponse> CreateStaffAsync(
        string fullName, string email, string passwordHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw ServiceException.Invalid("fullName", "Full name is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw ServiceException.Invalid("email", "Email is required.");
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw ServiceException.Invalid("password", "Password hash is required.");

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (await repository.FindByEmailAsync(normalizedEmail, cancellationToken) is not null)
            throw ServiceException.Invalid("email", "Email already exists.");

        var account = new SystemAccount
        {
            FullName = fullName.Trim(),
            Email = normalizedEmail,
            PasswordHash = passwordHash,
            Role = 0,
            CreatedDate = DateTime.UtcNow
        };
        repository.Add(account);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return account.ToResponse();
    }

    public async Task<AccountResponse> UpdateAsync(
        int id, string? fullName, short? role, CancellationToken cancellationToken = default)
    {
        // A2-07 must lock the Admin set and this mutation in one DB transaction
        // before exposing role updates through an HTTP endpoint.
        if (fullName is null && role is null)
            throw ServiceException.Invalid("operation", "Supply a name or role.");
        if (fullName is not null && string.IsNullOrWhiteSpace(fullName))
            throw ServiceException.Invalid("fullName", "Full name is required.");
        if (role is not null && role is not (0 or 1))
            throw ServiceException.Invalid("role", "Role must be Staff (0) or Admin (1).");

        var account = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        if (account.Role == 1 && role == 0 && await repository.CountAdminsAsync(cancellationToken) <= 1)
            throw ServiceException.Blocked("The last Admin cannot be demoted.");

        if (fullName is not null) account.FullName = fullName.Trim();
        if (role.HasValue) account.Role = role.Value;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return account.ToResponse();
    }

    public async Task DeleteAsync(int id, int actingAccountId, CancellationToken cancellationToken = default)
    {
        // A2-07 must make the last-Admin check and delete atomic before exposing it.
        var account = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        if (id == actingAccountId)
            throw ServiceException.Blocked("The current Admin cannot delete their own account.");
        if (await repository.HasCreatedTasksAsync(id, cancellationToken))
            throw ServiceException.Blocked("Cannot delete an account that created tasks.");
        if (account.Role == 1 && await repository.CountAdminsAsync(cancellationToken) <= 1)
            throw ServiceException.Blocked("The last Admin cannot be deleted.");

        repository.Remove(account);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
