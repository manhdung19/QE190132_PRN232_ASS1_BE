using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskTrack.Repo.Repositories;
using TaskTrack.Service.Errors;

namespace TaskTrack.API.Errors;

public static class ApiErrors
{
    public static string FieldName(string key)
    {
        if (string.IsNullOrEmpty(key) || key == "$") return "body";
        return string.Join('.', key.TrimStart('$', '.').Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
    }

    public static IActionResult InvalidModelState(ActionContext context)
    {
        var errors = context.ModelState.Where(x => x.Value?.Errors.Count > 0)
            .GroupBy(x => FieldName(x.Key))
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Value!.Errors)
                .Select(SafeMessage).Distinct().ToArray());
        return new BadRequestObjectResult(Create(context.HttpContext, 400, "Validation failed", errors))
        { ContentTypes = { "application/problem+json" } };
    }

    private static string SafeMessage(ModelError error) => error.Exception is null && !string.IsNullOrWhiteSpace(error.ErrorMessage)
        ? error.ErrorMessage : "Invalid value or JSON format.";

    public static ProblemDetails Create(HttpContext context, int status, string title, IDictionary<string, string[]>? errors = null)
    {
        ProblemDetails problem = errors is null ? new ProblemDetails() : new ValidationProblemDetails(errors);
        problem.Status = status;
        problem.Title = title;
        problem.Type = "about:blank";
        problem.Instance = context.Request.Path;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        return problem;
    }
}

// Handles exceptions before developer exception pages can expose implementation details.
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var problem = exception switch
            {
                ServiceException { Kind: ServiceErrorKind.NotFound } => ApiErrors.Create(context, 404, "Resource not found"),
                ServiceException business => ApiErrors.Create(context, 400, "Validation failed", business.Errors),
                PersistenceConflictException conflict => ApiErrors.Create(context, 400, "Validation failed",
                    new Dictionary<string, string[]> { [conflict.Field] = [conflict.Kind == PersistenceConflictKind.Unique
                        ? "The value already exists." : "The operation conflicts with related data."] }),
                _ => ApiErrors.Create(context, 500, "An unexpected error occurred")
            };
            if (problem.Status == 500)
                logger.LogError("Unhandled API error. TraceId: {TraceId}; Type: {ExceptionType}",
                    context.TraceIdentifier, exception.GetType().Name);
            context.Response.Clear();
            context.Response.StatusCode = problem.Status!.Value;
            await context.Response.WriteAsJsonAsync((object)problem, options: null,
                contentType: "application/problem+json", cancellationToken: context.RequestAborted);
        }
    }
}
