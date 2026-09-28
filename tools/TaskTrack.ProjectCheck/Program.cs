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
    var connection = new NpgsqlConnectionStringBuilder(rawConnectionString)
    {
        Options = "-c default_transaction_read_only=on"
    };
    var checks = 0;
    void Check(bool condition, string label)
    {
        if (!condition) throw new CheckFailure(label);
        checks++;
    }

    // Direct repository checks against real PostgreSQL.
    var options = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(connection.ConnectionString).Options;
    await using (var db = new TaskManagementDbContext(options))
    {
        var readOnly = await db.Database.SqlQueryRaw<string>("SELECT current_setting('default_transaction_read_only') AS \"Value\"").SingleAsync();
        Check(readOnly == "on", "Read-only PostgreSQL session for baseline checks");
        var repository = new ProjectRepository(db);
        var activeProjects = await repository.GetActiveAsync();
        Check(activeProjects.Count == 6, "Seed active project count is 6");
        Check(activeProjects.All(p => p.IsActive && !string.IsNullOrWhiteSpace(p.Department.DepartmentName)), "All active projects have Department eagerly loaded");
        var detail = await repository.GetActiveDetailAsync(1);
        Check(detail is not null && detail.ProjectId == 1 && detail.Department.DepartmentName == "Engineering", "Project 1 detail eagerly loaded");
        Check(detail!.Tasks.Count > 0 && detail.Tasks.All(t => t.IsActive), "Project 1 tasks loaded and active");
        Check(!db.ChangeTracker.Entries().Any(), "Read queries do not track entities");
    }

    // Start API host.
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
    Check(ready, "Actual API starts");

    async Task<JsonElement> Get(string path, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == status, "HTTP " + path);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    // 1. GET /api/projects: Active projects list
    var all = await Get("/api/projects");
    Check(all.GetArrayLength() == 6, "HTTP 6 active projects");
    Check(all.EnumerateArray().Select(x => x.GetProperty("projectId").GetInt32()).SequenceEqual([1, 2, 3, 4, 5, 6]), "Stable order and inactive excluded");
    Check(all.EnumerateArray().All(x => x.GetProperty("isActive").GetBoolean()
        && !string.IsNullOrWhiteSpace(x.GetProperty("departmentName").GetString())
        && !x.TryGetProperty("tasks", out _)
        && !x.TryGetProperty("department", out _)), "ProjectListItem DTO shape with departmentName and without navigation cycles");

    // 2. GET /api/projects/{id}: Detail with active tasks and tags
    var project1 = await Get("/api/projects/1");
    Check(project1.GetProperty("projectId").GetInt32() == 1, "Project 1 detail ID");
    Check(project1.GetProperty("projectName").GetString() == "Portal Redesign", "Project 1 name");
    Check(project1.GetProperty("departmentName").GetString() == "Engineering", "Project 1 departmentName");
    var tasks = project1.GetProperty("tasks");
    Check(tasks.GetArrayLength() > 0, "Project 1 has tasks");
    Check(tasks.EnumerateArray().All(t => t.GetProperty("isActive").GetBoolean()
        && t.GetProperty("projectId").GetInt32() == 1
        && t.TryGetProperty("tags", out _)
        && !t.TryGetProperty("project", out _)), "Tasks shape has tags and no navigation cycles");
    foreach (var missingId in new[] { 7, int.MaxValue, 0, -1 })
    {
        var missing = await Get($"/api/projects/{missingId}", HttpStatusCode.NotFound);
        Check(missing.GetProperty("status").GetInt32() == 404 && missing.TryGetProperty("traceId", out _), $"Inactive/missing Project {missingId} returns 404 ProblemDetails");
    }

    // 3. GET /api/projects/department/{departmentId}: By Department
    var dept1Projects = await Get("/api/projects/department/1");
    Check(dept1Projects.GetArrayLength() == 2, "Department 1 has 2 active projects");
    Check(dept1Projects.EnumerateArray().Select(x => x.GetProperty("projectId").GetInt32()).SequenceEqual([1, 2]), "Department 1 projects ordered [1, 2]");
    Check(dept1Projects.EnumerateArray().All(x => x.GetProperty("departmentName").GetString() == "Engineering"), "Department 1 project departmentName matches");
    var dept5Projects = await Get("/api/projects/department/5");
    Check(dept5Projects.GetArrayLength() == 1 && dept5Projects[0].GetProperty("projectId").GetInt32() == 6, "Department 5 has Cloud Migration (ID 6)");
    foreach (var missingDeptId in new[] { 6, 999999, 0, -1 })
    {
        var missingDept = await Get($"/api/projects/department/{missingDeptId}", HttpStatusCode.NotFound);
        Check(missingDept.GetProperty("status").GetInt32() == 404, $"Inactive/missing Department {missingDeptId} returns 404");
    }

    // 4. GET /api/projects/search: Search and filter combinations
    foreach (var suffix in new[] { "", "?name=", "?name=%20%20" })
    {
        var found = await Get("/api/projects/search" + suffix);
        Check(found.GetRawText() == all.GetRawText(), "Empty search equals active list");
    }
    var partialName = await Get("/api/projects/search?name=%20pORt%20");
    Check(partialName.GetArrayLength() == 1 && partialName[0].GetProperty("projectId").GetInt32() == 1, "Case-insensitive trimmed substring search");

    var statusFilter = await Get("/api/projects/search?status=1");
    Check(statusFilter.GetArrayLength() > 0 && statusFilter.EnumerateArray().All(x => x.GetProperty("status").GetInt32() == 1), "Status filter matches");

    var deptFilter = await Get("/api/projects/search?departmentId=1");
    Check(deptFilter.GetArrayLength() == 2 && deptFilter.EnumerateArray().Select(x => x.GetProperty("projectId").GetInt32()).SequenceEqual([1, 2]), "Search by departmentId matches");

    var combined = await Get("/api/projects/search?name=Portal&status=1&departmentId=1");
    Check(combined.GetArrayLength() == 1 && combined[0].GetProperty("projectId").GetInt32() == 1, "Combined AND filter matches");

    var noMatch = await Get("/api/projects/search?name=Portal&status=2");
    Check(noMatch.GetArrayLength() == 0, "No match returns empty array []");

    var nonExistentDeptSearch = await Get("/api/projects/search?departmentId=999999");
    Check(nonExistentDeptSearch.GetArrayLength() == 0, "Search with non-existent departmentId returns []");

    foreach (var term in new[] { "SEO", "not-a-project", "%", "_", "\\", "' OR 1=1 --" })
    {
        var found = await Get("/api/projects/search?name=" + Uri.EscapeDataString(term));
        Check(found.GetArrayLength() == 0, "No match/inactive/escaped metacharacters return []");
    }

    // Search validation errors (400)
    foreach (var invalidQuery in new[] { "?status=4", "?status=-1", "?departmentId=0", "?departmentId=-1" })
    {
        var err = await Get("/api/projects/search" + invalidQuery, HttpStatusCode.BadRequest);
        Check(err.GetProperty("status").GetInt32() == 400 && err.TryGetProperty("errors", out _), "Validation 400 on invalid search query " + invalidQuery);
    }

    // Route constraint
    using var invalidRoute = await client.GetAsync("/api/projects/not-an-int");
    Check(invalidRoute.StatusCode == HttpStatusCode.NotFound, "Integer route constraint on projects/{id}");
    using var invalidDeptRoute = await client.GetAsync("/api/projects/department/not-an-int");
    Check(invalidDeptRoute.StatusCode == HttpStatusCode.NotFound, "Integer route constraint on department/{departmentId}");

    // Swagger verification
    var swagger = await Get("/swagger/v1/swagger.json");
    var paths = swagger.GetProperty("paths");
    foreach (var path in new[] { "/api/projects", "/api/projects/{id}", "/api/projects/department/{departmentId}", "/api/projects/search" })
        Check(paths.GetProperty(path).TryGetProperty("get", out _), "Swagger GET " + path);
    using var ui = await client.GetAsync("/swagger/index.html");
    Check(ui.IsSuccessStatusCode && (await ui.Content.ReadAsStringAsync()).Contains("Swagger UI"), "Swagger UI served");

    Console.WriteLine($"PASS: {checks} Project checks against real PostgreSQL; all 4 GET endpoints verified.");
    return 0;
}
catch (CheckFailure failure)
{
    Console.Error.WriteLine("FAILED: " + failure.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Project verification failed ({exception.GetType().Name}). Connection details withheld: {exception.Message}");
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
