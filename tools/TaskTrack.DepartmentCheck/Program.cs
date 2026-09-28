using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskTrack.Repo.Configuration;
using TaskTrack.Repo.Data;
using TaskTrack.Repo.Repositories;

// Run from Backend after building the API. Real PostgreSQL, read-only sessions only.
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
    var connection = new NpgsqlConnectionStringBuilder(PostgresConnection.FromUrl(url))
    {
        Options = "-c default_transaction_read_only=on"
    };
    var checks = 0;
    void Check(bool condition, string label)
    {
        if (!condition) throw new CheckFailure(label);
        checks++;
    }

    // Exercise the repository directly to verify materialization and tracking.
    var options = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(connection.ConnectionString).Options;
    await using (var db = new TaskManagementDbContext(options))
    {
        var readOnly = await db.Database.SqlQueryRaw<string>("SELECT current_setting('default_transaction_read_only') AS \"Value\"").SingleAsync();
        Check(readOnly == "on", "Read-only PostgreSQL session");
        var repository = new DepartmentRepository(db);
        var departments = await repository.GetActiveAsync();
        Check(departments.Count == 5, "Seed active department count");
        var detail = await repository.GetActiveDetailAsync(1);
        Check(detail?.Projects.Count == 2 && detail.Projects.All(p => p.Department == detail), "Filtered include and inverse mapping");
        Check(!db.ChangeTracker.Entries().Any(), "Read queries do not track entities");
    }

    // Start the actual API entry point (not a replacement test host).
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
    start.Environment["ConnectionStrings__TaskTrack"] = connection.ConnectionString;
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ASPNETCORE_URLS"] = address;
    start.Environment["Logging__LogLevel__Default"] = "None";
    api = Process.Start(start) ?? throw new CheckFailure("API process startup");
    // Drain output without exposing credentials or provider diagnostics.
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
    Check(ready, "Actual API starts");
    async Task<JsonElement> Get(string path, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == status, "HTTP " + path);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
    var all = await Get("/api/departments");
    Check(all.GetArrayLength() == 5, "HTTP five active departments");
    Check(all.EnumerateArray().Select(x => x.GetProperty("departmentId").GetInt32()).SequenceEqual([1, 2, 3, 4, 5]), "Stable order and inactive excluded");
    Check(all.EnumerateArray().All(x => x.GetProperty("isActive").GetBoolean() && !x.TryGetProperty("projects", out _)), "List DTO shape");
    var department = await Get("/api/departments/1");
    var projects = department.GetProperty("projects");
    Check(projects.GetArrayLength() == 2, "Engineering projects included");
    Check(projects.EnumerateArray().Select(x => x.GetProperty("projectId").GetInt32()).SequenceEqual([1, 2]), "Projects ordered");
    Check(projects.EnumerateArray().All(x => x.GetProperty("isActive").GetBoolean()
        && x.GetProperty("departmentName").GetString() == "Engineering" && !x.TryGetProperty("department", out _)), "Project DTOs without navigation cycles");
    foreach (var id in new[] { 6, int.MaxValue, 0, -1 })
    {
        var missing = await Get($"/api/departments/{id}", HttpStatusCode.NotFound);
        Check(missing.GetProperty("status").GetInt32() == 404 && missing.TryGetProperty("traceId", out _), "Inactive/missing ProblemDetails");
    }
    foreach (var suffix in new[] { "", "?name=", "?name=%20%20" })
    {
        var found = await Get("/api/departments/search" + suffix);
        Check(found.GetRawText() == all.GetRawText(), "Empty search equals active list");
    }
    var partial = await Get("/api/departments/search?name=%20gINE%20");
    Check(partial.GetArrayLength() == 1 && partial[0].GetProperty("departmentId").GetInt32() == 1, "Trimmed case-insensitive substring");
    foreach (var term in new[] { "Marketing", "not-a-department", "%", "_", "\\", "' OR 1=1 --" })
    {
        var found = await Get("/api/departments/search?name=" + Uri.EscapeDataString(term));
        Check(found.GetArrayLength() == 0, "No match/inactive/literal metacharacters");
    }
    using var invalidRoute = await client.GetAsync("/api/departments/not-an-int");
    Check(invalidRoute.StatusCode == HttpStatusCode.NotFound, "Integer route constraint");
    var swagger = await Get("/swagger/v1/swagger.json");
    var paths = swagger.GetProperty("paths");
    foreach (var path in new[] { "/api/departments", "/api/departments/{id}", "/api/departments/search" })
        Check(paths.GetProperty(path).TryGetProperty("get", out _), "Swagger GET " + path);
    using var ui = await client.GetAsync("/swagger/index.html");
    Check(ui.IsSuccessStatusCode && (await ui.Content.ReadAsStringAsync()).Contains("Swagger UI"), "Swagger UI served");
    Console.WriteLine($"PASS: {checks} Department checks against real PostgreSQL; all database sessions read-only.");
    return 0;
}
catch (CheckFailure failure)
{
    Console.Error.WriteLine("FAILED: " + failure.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Department verification failed ({exception.GetType().Name}). Connection details withheld.");
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
