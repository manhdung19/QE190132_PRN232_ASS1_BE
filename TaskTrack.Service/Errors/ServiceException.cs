using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace TaskTrack.Service.Errors;

public enum ServiceErrorKind { NotFound, Validation, Blocked }

public sealed class ServiceException(ServiceErrorKind kind, IDictionary<string, string[]>? errors = null)
    : Exception(kind == ServiceErrorKind.NotFound ? "Resource not found." : "The operation is invalid.")
{
    public ServiceErrorKind Kind { get; } = kind;
    public IDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
    public static ServiceException Invalid(string field, string message) =>
        new(ServiceErrorKind.Validation, new Dictionary<string, string[]> { [field] = [message] });
    public static ServiceException Blocked(string message) =>
        new(ServiceErrorKind.Blocked, new Dictionary<string, string[]> { ["operation"] = [message] });
}

public static class RequestValidation
{
    public static void Validate(object request)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(request);
        Validator.TryValidateObject(request, context, results, validateAllProperties: true);
        // Also report cross-field errors when a property failed validation.
        if (request is IValidatableObject crossField)
            results.AddRange(crossField.Validate(context));
        if (results.Count == 0) return;
        var errors = results.SelectMany(result => result.MemberNames.DefaultIfEmpty("operation")
                .Select(field => (Field: JsonNamingPolicy.CamelCase.ConvertName(field), Message: result.ErrorMessage ?? "Invalid value.")))
            .GroupBy(x => x.Field).ToDictionary(g => g.Key, g => g.Select(x => x.Message).Distinct().ToArray());
        throw new ServiceException(ServiceErrorKind.Validation, errors);
    }
}
