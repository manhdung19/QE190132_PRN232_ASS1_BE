using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskTrack.Repo.Configuration;

Process? api = null;
try
{
    var url = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (args.Length == 2 && args[0] == "--secret-file")
    {
        var content = await File.ReadAllTextAsync(args[1]);
        var matches = Regex.Matches(content, "postgres(?:ql)?://[^\\s\"'`]+");
        if (matches.Count != 1) throw new InvalidOperationException("Invalid secret file format.");
        url = matches[0].Value;
    }
    if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Supply DATABASE_URL or --secret-file PATH.");
    var rawConnectionString = PostgresConnection.FromUrl(url);

    var checks = 0;
    void Check(bool condition, string label)
    {
        checks++;
        if (!condition) throw new CheckFailure(label);
    }

    // 1. Start API Host in Development mode.
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    var address = $"http://127.0.0.1:{port}";
    var apiDll = Path.GetFullPath("TaskTrack.API/bin/Debug/net8.0/TaskTrack.API.dll");
    if (!File.Exists(apiDll)) throw new CheckFailure("Build API from Backend before running this check");

    var start = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(apiDll)!
    };
    start.ArgumentList.Add(apiDll);
    start.Environment.Remove("DATABASE_URL");
    start.Environment["ConnectionStrings__TaskTrack"] = rawConnectionString;
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ASPNETCORE_URLS"] = address;
    start.Environment["Logging__LogLevel__Default"] = "None";

    api = Process.Start(start) ?? throw new CheckFailure("API process startup");
    api.OutputDataReceived += (_, _) => { };
    api.ErrorDataReceived += (_, _) => { };
    api.BeginOutputReadLine();
    api.BeginErrorReadLine();

    using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
    var ready = false;
    for (var attempt = 0; attempt < 60 && !api.HasExited; attempt++)
    {
        try
        {
            using var health = await client.GetAsync("/api/health");
            if (health.IsSuccessStatusCode) { ready = true; break; }
        }
        catch (HttpRequestException) { }
        await Task.Delay(250);
    }
    Check(ready, "API health check responds 200");

    // 2. Swagger Specification Verification
    using var swaggerResp = await client.GetAsync("/swagger/v1/swagger.json");
    Check(swaggerResp.IsSuccessStatusCode, "Swagger JSON endpoint responds 200");
    var swaggerJson = JsonDocument.Parse(await swaggerResp.Content.ReadAsStringAsync()).RootElement;

    // Title and Description checks
    var info = swaggerJson.GetProperty("info");
    Check(info.GetProperty("title").GetString() == "TaskTrack Management API", "Swagger title matches TaskTrack Management API");
    var desc = info.GetProperty("description").GetString() ?? "";
    Check(desc.Contains("ProjectStatus") && desc.Contains("TaskStatus") && desc.Contains("TaskPriority"), "Swagger documentation includes Enum specifications");
    Check(desc.Contains("TagIDs"), "Swagger documentation includes TagIDs specification");
    Check(desc.Contains("Soft delete only"), "Swagger documentation explains Soft delete rules");
    Check(desc.Contains("Department") && desc.Contains("Project") && desc.Contains("Tag"), "Swagger documentation explains deletion constraint rules");
    Check(desc.Contains("RFC 7807"), "Swagger documentation explains RFC 7807 ProblemDetails error contract");

    // Verify all 24 required business operations across 13 paths
    var paths = swaggerJson.GetProperty("paths");
    var requiredOperations = new (string Path, string Method)[]
    {
        // Department (6)
        ("/api/departments", "get"),
        ("/api/departments", "post"),
        ("/api/departments/{id}", "get"),
        ("/api/departments/{id}", "put"),
        ("/api/departments/{id}", "delete"),
        ("/api/departments/search", "get"),

        // Project (7)
        ("/api/projects", "get"),
        ("/api/projects", "post"),
        ("/api/projects/{id}", "get"),
        ("/api/projects/{id}", "put"),
        ("/api/projects/{id}", "delete"),
        ("/api/projects/department/{departmentId}", "get"),
        ("/api/projects/search", "get"),

        // Tag (4)
        ("/api/tags", "get"),
        ("/api/tags", "post"),
        ("/api/tags/{id}", "put"),
        ("/api/tags/{id}", "delete"),

        // Task (7)
        ("/api/tasks", "get"),
        ("/api/tasks", "post"),
        ("/api/tasks/{id}", "get"),
        ("/api/tasks/{id}", "put"),
        ("/api/tasks/{id}", "delete"),
        ("/api/tasks/project/{projectId}", "get"),
        ("/api/tasks/search", "get")
    };

    var foundOps = 0;
    foreach (var (path, method) in requiredOperations)
    {
        Check(paths.TryGetProperty(path, out var pathItem), $"Swagger path {path} exists");
        Check(pathItem.TryGetProperty(method, out var op), $"Swagger method {method.ToUpperInvariant()} on {path} exists");
        Check(!op.TryGetProperty("security", out _), $"Endpoint {method.ToUpperInvariant()} {path} requires no authentication (public)");
        foundOps++;
    }
    Check(foundOps == 24, "All 24 business endpoints verified in Swagger");

    // Swagger UI verification
    using var uiResp = await client.GetAsync("/swagger/index.html");
    Check(uiResp.IsSuccessStatusCode, "Swagger UI serves 200 OK");
    var uiHtml = await uiResp.Content.ReadAsStringAsync();
    Check(uiHtml.Contains("Swagger UI") || uiHtml.Contains("swagger-ui"), "Swagger UI HTML rendered properly");

    // 3. CORS Verification
    // Preflight OPTIONS with allowed origin localhost:3000
    using var preflightReq1 = new HttpRequestMessage(HttpMethod.Options, "/api/tasks");
    preflightReq1.Headers.Add("Origin", "http://localhost:3000");
    preflightReq1.Headers.Add("Access-Control-Request-Method", "POST");
    preflightReq1.Headers.Add("Access-Control-Request-Headers", "content-type");
    using var preflightResp1 = await client.SendAsync(preflightReq1);
    Check(preflightResp1.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigins1) &&
          allowOrigins1.Contains("http://localhost:3000"), "CORS preflight allows origin http://localhost:3000");
    Check(preflightResp1.Headers.Contains("Access-Control-Allow-Methods"), "CORS preflight specifies Access-Control-Allow-Methods");

    // Preflight OPTIONS with allowed origin 127.0.0.1:3000
    using var preflightReq2 = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
    preflightReq2.Headers.Add("Origin", "http://127.0.0.1:3000");
    preflightReq2.Headers.Add("Access-Control-Request-Method", "PUT");
    using var preflightResp2 = await client.SendAsync(preflightReq2);
    Check(preflightResp2.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigins2) &&
          allowOrigins2.Contains("http://127.0.0.1:3000"), "CORS preflight allows origin http://127.0.0.1:3000");

    // Direct request with allowed origin
    using var directReq = new HttpRequestMessage(HttpMethod.Get, "/api/departments");
    directReq.Headers.Add("Origin", "http://localhost:3000");
    using var directResp = await client.SendAsync(directReq);
    Check(directResp.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigins3) &&
          allowOrigins3.Contains("http://localhost:3000"), "CORS actual GET request includes Access-Control-Allow-Origin");

    // Preflight OPTIONS with untrusted origin
    using var untrustedPreflight = new HttpRequestMessage(HttpMethod.Options, "/api/tasks");
    untrustedPreflight.Headers.Add("Origin", "http://untrusted-external-site.com");
    untrustedPreflight.Headers.Add("Access-Control-Request-Method", "GET");
    using var untrustedResp1 = await client.SendAsync(untrustedPreflight);
    Check(!untrustedResp1.Headers.Contains("Access-Control-Allow-Origin"), "CORS preflight denies untrusted origin");

    // Direct request with untrusted origin
    using var untrustedDirect = new HttpRequestMessage(HttpMethod.Get, "/api/departments");
    untrustedDirect.Headers.Add("Origin", "http://untrusted-external-site.com");
    using var untrustedResp2 = await client.SendAsync(untrustedDirect);
    Check(!untrustedResp2.Headers.Contains("Access-Control-Allow-Origin"), "CORS actual request denies untrusted origin");

    // 4. RFC 7807 ProblemDetails and Error Contract Verification
    // 404 ProblemDetails structure check across GET endpoints
    foreach (var missingPath in new[] { "/api/departments/999999", "/api/projects/999999", "/api/tasks/999999" })
    {
        using var notFoundResp = await client.GetAsync(missingPath);
        Check(notFoundResp.StatusCode == HttpStatusCode.NotFound, $"GET {missingPath} returns 404");
        Check(notFoundResp.Content.Headers.ContentType?.MediaType == "application/problem+json", $"GET {missingPath} content-type is application/problem+json");
        var notFoundBody = JsonDocument.Parse(await notFoundResp.Content.ReadAsStringAsync()).RootElement;
        Check(notFoundBody.GetProperty("status").GetInt32() == 404, "ProblemDetails status is 404");
        Check(notFoundBody.GetProperty("title").GetString() == "Resource not found", "ProblemDetails title is 'Resource not found'");
        Check(notFoundBody.GetProperty("type").GetString() == "about:blank", "ProblemDetails type is 'about:blank'");
        Check(notFoundBody.GetProperty("instance").GetString() == missingPath, "ProblemDetails instance matches request path");
        Check(notFoundBody.TryGetProperty("traceId", out var traceId) && !string.IsNullOrWhiteSpace(traceId.GetString()), "ProblemDetails includes traceId");
    }

    // 404 on Tag DELETE (since Tag entity has no GET by ID endpoint)
    using var tagNotFoundResp = await client.DeleteAsync("/api/tags/999999");
    Check(tagNotFoundResp.StatusCode == HttpStatusCode.NotFound, "DELETE /api/tags/999999 returns 404");
    var tagNotFoundBody = JsonDocument.Parse(await tagNotFoundResp.Content.ReadAsStringAsync()).RootElement;
    Check(tagNotFoundBody.GetProperty("status").GetInt32() == 404, "Tag 404 status is 404");
    Check(tagNotFoundBody.GetProperty("title").GetString() == "Resource not found", "Tag 404 title is 'Resource not found'");


    // 400 Validation ProblemDetails structure check
    foreach (var (postPath, badBody, expectedField) in new[]
    {
        ("/api/departments", (object)new { departmentName = "", departmentDescription = "" }, "departmentName"),
        ("/api/projects", new { projectName = "", departmentId = 0 }, "projectName"),
        ("/api/tags", new { tagName = "", color = "invalid" }, "tagName"),
        ("/api/tasks", new { title = "", projectId = 0 }, "title")
    })
    {
        var postReq = new HttpRequestMessage(HttpMethod.Post, postPath) { Content = JsonContent.Create(badBody) };
        using var valResp = await client.SendAsync(postReq);
        Check(valResp.StatusCode == HttpStatusCode.BadRequest, $"POST {postPath} bad payload returns 400");
        Check(valResp.Content.Headers.ContentType?.MediaType == "application/problem+json", $"POST {postPath} content-type is application/problem+json");
        var valBody = JsonDocument.Parse(await valResp.Content.ReadAsStringAsync()).RootElement;
        Check(valBody.GetProperty("status").GetInt32() == 400, "Validation ProblemDetails status is 400");
        Check(valBody.GetProperty("title").GetString() == "Validation failed", "Validation ProblemDetails title is 'Validation failed'");
        Check(valBody.GetProperty("errors").TryGetProperty(expectedField, out _), $"Validation error contains camelCase field '{expectedField}'");
    }

    // Bad JSON syntax check (safe error without leaking exceptions/stack traces)
    var malformedReq = new HttpRequestMessage(HttpMethod.Post, "/api/tasks")
    {
        Content = new StringContent("{ broken json : true ", System.Text.Encoding.UTF8, "application/json")
    };
    using var malformedResp = await client.SendAsync(malformedReq);
    Check(malformedResp.StatusCode == HttpStatusCode.BadRequest, "Malformed JSON returns 400");
    var malformedRaw = await malformedResp.Content.ReadAsStringAsync();
    Check(!malformedRaw.Contains("Exception") && !malformedRaw.Contains("StackTrace") && !malformedRaw.Contains("Npgsql"), "Malformed JSON error does not leak exception details or SQL");
    var malformedBody = JsonDocument.Parse(malformedRaw).RootElement;
    Check(malformedBody.GetProperty("errors").TryGetProperty("body", out var bodyErrors) && bodyErrors.EnumerateArray().Any(), "Malformed JSON error mapped to 'body' field with safe message");

    // Blocked delete check (operation constraint)
    var delReq = new HttpRequestMessage(HttpMethod.Delete, "/api/projects/1");
    using var delResp = await client.SendAsync(delReq);
    Check(delResp.StatusCode == HttpStatusCode.BadRequest, "Blocked delete returns 400");
    var delBody = JsonDocument.Parse(await delResp.Content.ReadAsStringAsync()).RootElement;
    Check(delBody.GetProperty("errors").TryGetProperty("operation", out var opErrors) && opErrors.EnumerateArray().Any(), "Blocked delete mapped to 'operation' field");

    // Route constraint check: integer constraint rejects non-integers with 404
    foreach (var nonIntPath in new[] { "/api/departments/not-an-int", "/api/projects/not-an-int", "/api/tasks/not-an-int", "/api/tags/not-an-int" })
    {
        var method = nonIntPath.Contains("tags") ? HttpMethod.Delete : HttpMethod.Get;
        using var nonIntResp = await client.SendAsync(new HttpRequestMessage(method, nonIntPath));
        Check(nonIntResp.StatusCode == HttpStatusCode.NotFound, $"Non-integer path {nonIntPath} returns 404");
    }

    // Shut down dev api host
    if (!api.HasExited) { api.Kill(entireProcessTree: true); await api.WaitForExitAsync(); }
    api.Dispose();
    api = null;

    // 5. Production Swagger Toggle Verification
    // Verify Swagger is served in Production environment when Swagger:EnableInProduction = true
    var prodListener = new TcpListener(IPAddress.Loopback, 0);
    prodListener.Start();
    var prodPort = ((IPEndPoint)prodListener.LocalEndpoint).Port;
    prodListener.Stop();
    var prodAddress = $"http://127.0.0.1:{prodPort}";

    var prodStart = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(apiDll)!
    };
    prodStart.ArgumentList.Add(apiDll);
    prodStart.Environment.Remove("DATABASE_URL");
    prodStart.Environment["ConnectionStrings__TaskTrack"] = rawConnectionString;
    prodStart.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    prodStart.Environment["Swagger__EnableInProduction"] = "true";
    prodStart.Environment["ASPNETCORE_URLS"] = prodAddress;
    prodStart.Environment["Logging__LogLevel__Default"] = "None";

    api = Process.Start(prodStart) ?? throw new CheckFailure("Production API process startup");
    api.OutputDataReceived += (_, _) => { };
    api.ErrorDataReceived += (_, _) => { };
    api.BeginOutputReadLine();
    api.BeginErrorReadLine();

    using var prodClient = new HttpClient { BaseAddress = new Uri(prodAddress), Timeout = TimeSpan.FromSeconds(30) };
    var prodReady = false;
    for (var attempt = 0; attempt < 60 && !api.HasExited; attempt++)
    {
        try
        {
            using var health = await prodClient.GetAsync("/api/health");
            if (health.IsSuccessStatusCode) { prodReady = true; break; }
        }
        catch (HttpRequestException) { }
        await Task.Delay(250);
    }
    Check(prodReady, "Production API health check responds 200");

    using var prodSwaggerResp = await prodClient.GetAsync("/swagger/v1/swagger.json");
    Check(prodSwaggerResp.IsSuccessStatusCode, "Production Swagger JSON responds 200 with Swagger:EnableInProduction=true");

    using var prodSwaggerUi = await prodClient.GetAsync("/swagger/index.html");
    Check(prodSwaggerUi.IsSuccessStatusCode, "Production Swagger UI serves 200 with Swagger:EnableInProduction=true");

    Console.WriteLine($"PASS: {checks} API Contract, Swagger, CORS, and ProblemDetails checks verified.");
    return 0;
}
catch (CheckFailure failure)
{
    Console.Error.WriteLine("FAILED: " + failure.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"API Contract verification failed ({exception.GetType().Name}): {exception.Message}");
    return 1;
}
finally
{
    if (api is not null)
    {
        if (!api.HasExited) { api.Kill(entireProcessTree: true); await api.WaitForExitAsync(); }
        api.Dispose();
    }
}

internal sealed class CheckFailure(string message) : Exception(message);
