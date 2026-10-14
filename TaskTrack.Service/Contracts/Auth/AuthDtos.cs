using System.ComponentModel.DataAnnotations;

namespace TaskTrack.Service.Contracts;

public sealed class RegisterRequest : IValidatableObject
{
    [Required, StringLength(150)] public string FullName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(320)] public string Email { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(FullName))
            yield return new ValidationResult("Full name is required.", [nameof(FullName)]);
        if ((Password?.Length ?? 0) < 8)
            yield return new ValidationResult("Password must contain at least 8 characters.", [nameof(Password)]);
        if ((Password?.Length ?? 0) > 128)
            yield return new ValidationResult("Password must contain at most 128 characters.", [nameof(Password)]);
    }
}

public sealed class LoginRequest
{
    private string _email = string.Empty;
    [Required, EmailAddress] public string Email { get => _email; set => _email = value?.Trim() ?? string.Empty; }
    [Required] public string Password { get; set; } = string.Empty;
}
