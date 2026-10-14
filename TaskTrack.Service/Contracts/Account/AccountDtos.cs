namespace TaskTrack.Service.Contracts;

// API-facing account data. PasswordHash must never be added here.
public sealed record AccountResponse
{
    public int AccountId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public short Role { get; init; }
    public DateTime CreatedDate { get; init; }
}
