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
    start.Environment["ConnectionStrings__TaskTrack"] = PostgresConnection.FromUrl(url);
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
    async Task<(HttpStatusCode StatusCode, JsonElement Body, HttpResponseMessage Raw)> SendJson(HttpMethod method, string path, object? payload = null)
    {
        var message = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload);
            message.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }
        var response = await client.SendAsync(message);
        var content = await response.Content.ReadAsStringAsync();
        JsonElement root = default;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return (response.StatusCode, root, response);
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

    // Task 006: Validation errors on POST and PUT
    foreach (var badPayload in new object[] {
        new { departmentName = "", departmentDescription = "valid" },
        new { departmentName = "   ", departmentDescription = "valid" },
        new { departmentName = new string('A', 101), departmentDescription = "valid" },
        new { departmentName = "valid", departmentDescription = "" },
        new { departmentName = "valid", departmentDescription = "   " },
        new { departmentName = "valid", departmentDescription = new string('B', 301) }
    })
    {
        var (status, body, resp) = await SendJson(HttpMethod.Post, "/api/departments", badPayload);
        resp.Dispose();
        Check(status == HttpStatusCode.BadRequest, "Validation 400 on bad create payload");
        Check(body.TryGetProperty("errors", out _), "Validation ProblemDetails has errors");
    }

    // Task 006: Cannot delete department with projects (Department 1 has projects)
    var (delStatus, delBody, delResp) = await SendJson(HttpMethod.Delete, "/api/departments/1");
    delResp.Dispose();
    Check(delStatus == HttpStatusCode.BadRequest, "Blocked delete returns 400");
    Check(delBody.GetProperty("errors").TryGetProperty("operation", out _), "Blocked delete error maps to operation");
    var dept1AfterBlocked = await Get("/api/departments/1");
    Check(dept1AfterBlocked.GetProperty("departmentId").GetInt32() == 1, "Department 1 unchanged after blocked delete");

    // Task 006: Inactive or non-existent PUT / DELETE returns 404
    foreach (var missingId in new[] { 6, 999999, 0, -1 })
    {
        var (pStatus, pBody, pResp) = await SendJson(HttpMethod.Put, $"/api/departments/{missingId}",
            new { departmentName = "Updated", departmentDescription = "Updated Desc", isActive = true });
        pResp.Dispose();
        Check(pStatus == HttpStatusCode.NotFound && pBody.GetProperty("status").GetInt32() == 404, $"PUT {missingId} returns 404");

        var (dStatus, dBody, dResp) = await SendJson(HttpMethod.Delete, $"/api/departments/{missingId}");
        dResp.Dispose();
        Check(dStatus == HttpStatusCode.NotFound && dBody.GetProperty("status").GetInt32() == 404, $"DELETE {missingId} returns 404");
    }

    // Task 006: Complete lifecycle test on temporary department (Create -> Read -> Update -> Delete)
    var createPayload = new { departmentName = "QA Temp Department", departmentDescription = "Temporary QA Team for testing", isActive = true };
    var (createStatus, createBody, createResp) = await SendJson(HttpMethod.Post, "/api/departments", createPayload);
    Check(createStatus == HttpStatusCode.Created, "POST /api/departments returns 201 Created");
    var newId = createBody.GetProperty("departmentId").GetInt32();
    Check(newId > 0, "Created department has positive ID");
    Check(createBody.GetProperty("departmentName").GetString() == "QA Temp Department", "Created name matches");
    Check(createBody.GetProperty("projects").GetArrayLength() == 0, "Created department has empty projects");
    Check(createResp.Headers.Location is not null && createResp.Headers.Location.OriginalString.EndsWith($"/api/departments/{newId}"), "Location header points to new ID");
    createResp.Dispose();

    // Verify GET reads newly created department
    var getCreated = await Get($"/api/departments/{newId}");
    Check(getCreated.GetProperty("departmentName").GetString() == "QA Temp Department", "GET new department matches");

    // PUT updates newly created department
    var updatePayload = new { departmentName = "QA Automation Dept", departmentDescription = "Updated QA Automation Team", isActive = true };
    var (updateStatus, updateBody, updateResp) = await SendJson(HttpMethod.Put, $"/api/departments/{newId}", updatePayload);
    updateResp.Dispose();
    Check(updateStatus == HttpStatusCode.OK, "PUT /api/departments/{id} returns 200 OK");
    Check(updateBody.GetProperty("departmentName").GetString() == "QA Automation Dept", "Updated name matches");

    // Verify GET reads updated department
    var getUpdated = await Get($"/api/departments/{newId}");
    Check(getUpdated.GetProperty("departmentName").GetString() == "QA Automation Dept", "GET updated department matches");

    // DELETE cleans up newly created department
    var (deleteStatus, _, deleteResp) = await SendJson(HttpMethod.Delete, $"/api/departments/{newId}");
    deleteResp.Dispose();
    Check(deleteStatus == HttpStatusCode.NoContent, "DELETE returns 204 NoContent");

    // Verify GET returns 404 after deletion
    var (getDeletedStatus, _, getDeletedResp) = await SendJson(HttpMethod.Get, $"/api/departments/{newId}");
    getDeletedResp.Dispose();
    Check(getDeletedStatus == HttpStatusCode.NotFound, "GET after DELETE returns 404");

    // Verify active count is restored to exactly 5
    var finalAll = await Get("/api/departments");
    Check(finalAll.GetArrayLength() == 5, "Database restored: exactly 5 active departments");

    var swagger = await Get("/swagger/v1/swagger.json");
    var paths = swagger.GetProperty("paths");
    foreach (var path in new[] { "/api/departments", "/api/departments/{id}", "/api/departments/search" })
        Check(paths.GetProperty(path).TryGetProperty("get", out _), "Swagger GET " + path);
    Check(paths.GetProperty("/api/departments").TryGetProperty("post", out _), "Swagger POST /api/departments");
    Check(paths.GetProperty("/api/departments/{id}").TryGetProperty("put", out _), "Swagger PUT /api/departments/{id}");
    Check(paths.GetProperty("/api/departments/{id}").TryGetProperty("delete", out _), "Swagger DELETE /api/departments/{id}");
    using var ui = await client.GetAsync("/swagger/index.html");
    Check(ui.IsSuccessStatusCode && (await ui.Content.ReadAsStringAsync()).Contains("Swagger UI"), "Swagger UI served");
    Console.WriteLine($"PASS: {checks} Department checks against real PostgreSQL; write operations and cleanup verified.");
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
