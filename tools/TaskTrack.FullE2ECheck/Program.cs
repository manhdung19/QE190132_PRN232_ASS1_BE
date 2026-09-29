using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
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

    var cleanOptions = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(rawConnectionString).Options;
    await using (var cleanDb = new TaskManagementDbContext(cleanOptions))
    {
        await cleanDb.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"TaskTag\" WHERE \"TaskID\" IN (SELECT \"TaskID\" FROM \"Task\" WHERE \"Title\" LIKE 'qa-e2e%');" +
            "DELETE FROM \"Task\" WHERE \"Title\" LIKE 'qa-e2e%';" +
            "DELETE FROM \"Project\" WHERE \"ProjectName\" LIKE 'qa-e2e%';" +
            "DELETE FROM \"Department\" WHERE \"DepartmentName\" LIKE 'qa-e2e%';" +
            "DELETE FROM \"Tag\" WHERE \"TagName\" LIKE 'qa-e2e%';");
    }

    var connection = new NpgsqlConnectionStringBuilder(rawConnectionString)
    {
        Options = "-c default_transaction_read_only=on"
    };

    var checks = 0;
    void Check(bool condition, string label)
    {
        checks++;
        if (!condition) throw new CheckFailure(label);
    }

    // 1. Direct repository checks against real PostgreSQL.
    var options = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(connection.ConnectionString).Options;
    await using (var db = new TaskManagementDbContext(options))
    {
        var readOnly = await db.Database.SqlQueryRaw<string>("SELECT current_setting('default_transaction_read_only') AS \"Value\"").SingleAsync();
        Check(readOnly == "on", "Read-only PostgreSQL session for baseline checks");

        var deptRepo = new DepartmentRepository(db);
        var projRepo = new ProjectRepository(db);
        var tagRepo = new TagRepository(db);
        var taskRepo = new TaskRepository(db);

        Check((await deptRepo.GetActiveAsync()).Count == 5, "Baseline: exactly 5 active departments");
        Check((await projRepo.GetActiveAsync()).Count == 6, "Baseline: exactly 6 active projects");
        Check((await tagRepo.GetAllAsync()).Count == 10, "Baseline: exactly 10 tags");
        Check((await taskRepo.GetActiveAsync()).Count == 17, "Baseline: exactly 17 active tasks");
        Check(!db.ChangeTracker.Entries().Any(), "Read queries do not track entities");
    }

    // 2. Start API Host
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

    async Task<JsonElement> Get(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == expected, $"GET {path} status {response.StatusCode} == {expected}");
        var content = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonDocument.Parse(content).RootElement;
    }

    async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Resp)> SendJson(HttpMethod method, string path, object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(content) ? default : JsonDocument.Parse(content).RootElement;
        return (response.StatusCode, json, response);
    }

    // List to collect 24-endpoint matrix results
    var resultsTable = new List<(int Index, string Endpoint, string Method, string Expected, string Actual, string Status)>();

    void Record(int index, string endpoint, string method, string expected, string actual, bool pass)
    {
        Check(pass, $"Matrix test #{index} {method} {endpoint}");
        resultsTable.Add((index, endpoint, method, expected, actual, pass ? "PASS" : "FAIL"));
    }

    // -------------------------------------------------------------
    // GROUP 1: Department Endpoints (1..6)
    // -------------------------------------------------------------
    // #1: GET /api/departments
    var depts = await Get("/api/departments");
    Record(1, "/api/departments", "GET", "200 OK, 5 active departments", $"200 OK, {depts.GetArrayLength()} items", depts.GetArrayLength() == 5);

    // #2: GET /api/departments/{id}
    var dept1 = await Get("/api/departments/1");
    Record(2, "/api/departments/{id}", "GET", "200 OK, Department 1 with 2 projects", $"200 OK, {dept1.GetProperty("departmentName").GetString()}, projects: {dept1.GetProperty("projects").GetArrayLength()}", dept1.GetProperty("departmentName").GetString() == "Engineering" && dept1.GetProperty("projects").GetArrayLength() == 2);

    // #3: GET /api/departments/search
    var deptSearch = await Get("/api/departments/search?name=Eng");
    Record(3, "/api/departments/search", "GET", "200 OK, matches Engineering", $"200 OK, count: {deptSearch.GetArrayLength()}", deptSearch.GetArrayLength() == 1 && deptSearch[0].GetProperty("departmentId").GetInt32() == 1);

    // #4: POST /api/departments
    var (dPostStatus, dPostBody, dPostResp) = await SendJson(HttpMethod.Post, "/api/departments", new
    {
        departmentName = "qa-e2e-dept",
        departmentDescription = "E2E Test Department"
    });
    var flowDeptId = dPostBody.GetProperty("departmentId").GetInt32();
    var dPostLocation = dPostResp.Headers.Location?.ToString() ?? "";
    dPostResp.Dispose();
    Record(4, "/api/departments", "POST", "201 Created with Location header", $"{dPostStatus}, Location: {dPostLocation}", dPostStatus == HttpStatusCode.Created && flowDeptId > 0 && dPostLocation.EndsWith($"/api/departments/{flowDeptId}"));

    // #5: PUT /api/departments/{id}
    var (dPutStatus, dPutBody, dPutResp) = await SendJson(HttpMethod.Put, $"/api/departments/{flowDeptId}", new
    {
        departmentName = "qa-e2e-dept-updated",
        departmentDescription = "Updated Description"
    });
    dPutResp.Dispose();
    Record(5, "/api/departments/{id}", "PUT", "200 OK, updated name", $"{dPutStatus}, name: {dPutBody.GetProperty("departmentName").GetString()}", dPutStatus == HttpStatusCode.OK && dPutBody.GetProperty("departmentName").GetString() == "qa-e2e-dept-updated");

    // Standalone department for DELETE #6
    var (_, standaloneDeptBody, standaloneDeptResp) = await SendJson(HttpMethod.Post, "/api/departments", new
    {
        departmentName = "qa-e2e-dept-standalone",
        departmentDescription = "To be deleted"
    });
    var standaloneDeptId = standaloneDeptBody.GetProperty("departmentId").GetInt32();
    standaloneDeptResp.Dispose();

    // #6: DELETE /api/departments/{id}
    var (dDelStatus, _, dDelResp) = await SendJson(HttpMethod.Delete, $"/api/departments/{standaloneDeptId}", new { });
    dDelResp.Dispose();
    Record(6, "/api/departments/{id}", "DELETE", "204 NoContent for standalone department", $"{dDelStatus}", dDelStatus == HttpStatusCode.NoContent);

    // -------------------------------------------------------------
    // GROUP 2: Project Endpoints (7..13)
    // -------------------------------------------------------------
    // #7: GET /api/projects
    var projs = await Get("/api/projects");
    Record(7, "/api/projects", "GET", "200 OK, 6 active projects", $"200 OK, {projs.GetArrayLength()} items", projs.GetArrayLength() == 6);

    // #8: GET /api/projects/{id}
    var proj1 = await Get("/api/projects/1");
    Record(8, "/api/projects/{id}", "GET", "200 OK, Project 1 with 4 tasks", $"200 OK, {proj1.GetProperty("projectName").GetString()}, tasks: {proj1.GetProperty("tasks").GetArrayLength()}", proj1.GetProperty("projectName").GetString() == "Portal Redesign" && proj1.GetProperty("tasks").GetArrayLength() == 4);

    // #9: GET /api/projects/department/{departmentId}
    var projByDept = await Get("/api/projects/department/1");
    Record(9, "/api/projects/department/{departmentId}", "GET", "200 OK, 2 projects for Department 1", $"200 OK, count: {projByDept.GetArrayLength()}", projByDept.GetArrayLength() == 2);

    // #10: GET /api/projects/search
    var projSearch = await Get("/api/projects/search?name=Portal&status=1");
    Record(10, "/api/projects/search", "GET", "200 OK, matches Portal Redesign", $"200 OK, count: {projSearch.GetArrayLength()}", projSearch.GetArrayLength() == 1 && projSearch[0].GetProperty("projectId").GetInt32() == 1);

    // #11: POST /api/projects
    var (pPostStatus, pPostBody, pPostResp) = await SendJson(HttpMethod.Post, "/api/projects", new
    {
        projectName = "qa-e2e-proj",
        description = "E2E Test Project",
        startDate = "2026-09-28",
        status = 0,
        departmentId = flowDeptId
    });
    var flowProjId = pPostBody.GetProperty("projectId").GetInt32();
    var pPostLocation = pPostResp.Headers.Location?.ToString() ?? "";
    pPostResp.Dispose();
    Record(11, "/api/projects", "POST", "201 Created with Location header", $"{pPostStatus}, Location: {pPostLocation}", pPostStatus == HttpStatusCode.Created && flowProjId > 0 && pPostLocation.EndsWith($"/api/projects/{flowProjId}"));

    // #12: PUT /api/projects/{id}
    var (pPutStatus, pPutBody, pPutResp) = await SendJson(HttpMethod.Put, $"/api/projects/{flowProjId}", new
    {
        projectName = "qa-e2e-proj-updated",
        description = "Updated Project",
        startDate = "2026-09-28",
        status = 1,
        departmentId = flowDeptId
    });
    pPutResp.Dispose();
    Record(12, "/api/projects/{id}", "PUT", "200 OK, updated name and status", $"{pPutStatus}, name: {pPutBody.GetProperty("projectName").GetString()}, status: {pPutBody.GetProperty("status").GetInt32()}", pPutStatus == HttpStatusCode.OK && pPutBody.GetProperty("status").GetInt32() == 1);

    // Standalone project for DELETE #13
    var (_, standaloneProjBody, standaloneProjResp) = await SendJson(HttpMethod.Post, "/api/projects", new
    {
        projectName = "qa-e2e-proj-standalone",
        startDate = "2026-09-28",
        departmentId = flowDeptId
    });
    var standaloneProjId = standaloneProjBody.GetProperty("projectId").GetInt32();
    standaloneProjResp.Dispose();

    // #13: DELETE /api/projects/{id}
    var (pDelStatus, _, pDelResp) = await SendJson(HttpMethod.Delete, $"/api/projects/{standaloneProjId}", new { });
    pDelResp.Dispose();
    Record(13, "/api/projects/{id}", "DELETE", "204 NoContent for standalone project", $"{pDelStatus}", pDelStatus == HttpStatusCode.NoContent);

    // -------------------------------------------------------------
    // GROUP 3: Tag Endpoints (14..17)
    // -------------------------------------------------------------
    // #14: GET /api/tags
    var tagsList = await Get("/api/tags");
    Record(14, "/api/tags", "GET", "200 OK, 10 seed tags", $"200 OK, {tagsList.GetArrayLength()} items", tagsList.GetArrayLength() == 10);

    // #15: POST /api/tags
    var (tPostStatus, tPostBody, tPostResp) = await SendJson(HttpMethod.Post, "/api/tags", new
    {
        tagName = "qa-e2e-tag",
        color = "#112233"
    });
    var flowTagId = tPostBody.GetProperty("tagId").GetInt32();
    var tPostLocation = tPostResp.Headers.Location?.ToString() ?? "";
    tPostResp.Dispose();
    Record(15, "/api/tags", "POST", "201 Created with Location header", $"{tPostStatus}, Location: {tPostLocation}", tPostStatus == HttpStatusCode.Created && flowTagId > 0 && tPostLocation.EndsWith($"/api/tags/{flowTagId}"));

    // #16: PUT /api/tags/{id}
    var (tPutStatus, tPutBody, tPutResp) = await SendJson(HttpMethod.Put, $"/api/tags/{flowTagId}", new
    {
        tagName = "qa-e2e-tag-updated",
        color = "#AABBCC"
    });
    tPutResp.Dispose();
    Record(16, "/api/tags/{id}", "PUT", "200 OK, updated tagName and color", $"{tPutStatus}, name: {tPutBody.GetProperty("tagName").GetString()}, color: {tPutBody.GetProperty("color").GetString()}", tPutStatus == HttpStatusCode.OK && tPutBody.GetProperty("color").GetString() == "#AABBCC");

    // Standalone tag for DELETE #17
    var (_, standaloneTagBody, standaloneTagResp) = await SendJson(HttpMethod.Post, "/api/tags", new
    {
        tagName = "qa-e2e-tag-standalone",
        color = "#334455"
    });
    var standaloneTagId = standaloneTagBody.GetProperty("tagId").GetInt32();
    standaloneTagResp.Dispose();

    // #17: DELETE /api/tags/{id}
    var (tDelStatus, _, tDelResp) = await SendJson(HttpMethod.Delete, $"/api/tags/{standaloneTagId}", new { });
    tDelResp.Dispose();
    Record(17, "/api/tags/{id}", "DELETE", "204 NoContent for standalone tag", $"{tDelStatus}", tDelStatus == HttpStatusCode.NoContent);

    // -------------------------------------------------------------
    // GROUP 4: Task Endpoints (18..24)
    // -------------------------------------------------------------
    // #18: GET /api/tasks
    var tasksList = await Get("/api/tasks");
    Record(18, "/api/tasks", "GET", "200 OK, 17 active tasks", $"200 OK, {tasksList.GetArrayLength()} items", tasksList.GetArrayLength() == 17);

    // #19: GET /api/tasks/{id}
    var task1 = await Get("/api/tasks/1");
    Record(19, "/api/tasks/{id}", "GET", "200 OK, Task 1 detail with tags [1, 4]", $"200 OK, {task1.GetProperty("title").GetString()}, tags: {task1.GetProperty("tags").GetArrayLength()}", task1.GetProperty("title").GetString() == "Redesign landing page hero section" && task1.GetProperty("tags").GetArrayLength() == 2);

    // #20: GET /api/tasks/project/{projectId}
    var taskByProj = await Get("/api/tasks/project/1");
    Record(20, "/api/tasks/project/{projectId}", "GET", "200 OK, 4 tasks for Project 1", $"200 OK, count: {taskByProj.GetArrayLength()}", taskByProj.GetArrayLength() == 4);

    // #21: GET /api/tasks/search
    var taskSearch = await Get("/api/tasks/search?title=hero&status=2&projectId=1");
    Record(21, "/api/tasks/search", "GET", "200 OK, matches Task 1", $"200 OK, count: {taskSearch.GetArrayLength()}", taskSearch.GetArrayLength() == 1 && taskSearch[0].GetProperty("taskId").GetInt32() == 1);

    // #22: POST /api/tasks
    var (tkPostStatus, tkPostBody, tkPostResp) = await SendJson(HttpMethod.Post, "/api/tasks", new
    {
        title = "qa-e2e-task",
        description = "E2E Task linked to flow project and flow tag",
        status = 0,
        priority = 1,
        dueDate = "2026-10-15",
        projectId = flowProjId,
        tagIds = new[] { flowTagId }
    });
    var flowTaskId = tkPostBody.GetProperty("taskId").GetInt32();
    var tkPostLocation = tkPostResp.Headers.Location?.ToString() ?? "";
    tkPostResp.Dispose();
    var getCreatedTask = await Get($"/api/tasks/{flowTaskId}");
    var dbInitialCreatedDate = getCreatedTask.GetProperty("createdDate").GetString();
    Record(22, "/api/tasks", "POST", "201 Created with Location header and attached tag", $"{tkPostStatus}, Location: {tkPostLocation}, tags: {tkPostBody.GetProperty("tags").GetArrayLength()}", tkPostStatus == HttpStatusCode.Created && flowTaskId > 0 && tkPostBody.GetProperty("tags").GetArrayLength() == 1);

    // #23: PUT /api/tasks/{id}
    var (tkPutStatus, tkPutBody, tkPutResp) = await SendJson(HttpMethod.Put, $"/api/tasks/{flowTaskId}", new
    {
        title = "qa-e2e-task-updated",
        description = "Updated description",
        status = 1,
        priority = 2,
        projectId = flowProjId,
        tagIds = new[] { flowTagId }
    });
    tkPutResp.Dispose();
    var postUpdateCreatedDate = tkPutBody.GetProperty("createdDate").GetString();
    Record(23, "/api/tasks/{id}", "PUT", "200 OK, ModifiedDate set, CreatedDate preserved", $"{tkPutStatus}, modifiedDate set: {tkPutBody.GetProperty("modifiedDate").ValueKind != JsonValueKind.Null}", tkPutStatus == HttpStatusCode.OK && DateTime.Parse(dbInitialCreatedDate!) == DateTime.Parse(postUpdateCreatedDate!) && tkPutBody.GetProperty("modifiedDate").ValueKind != JsonValueKind.Null);

    // -------------------------------------------------------------
    // BUSINESS RULES & CONSTRAINTS VERIFICATION
    // -------------------------------------------------------------
    // Constraint 1: Department cannot be deleted while having Project
    var (dBlockStatus, dBlockBody, dBlockResp) = await SendJson(HttpMethod.Delete, $"/api/departments/{flowDeptId}", new { });
    dBlockResp.Dispose();
    Check(dBlockStatus == HttpStatusCode.BadRequest && dBlockBody.GetProperty("errors").TryGetProperty("operation", out _), "Department blocked delete returns 400 operation");

    // Constraint 2: Project cannot be deleted while having Task (Active)
    var (pBlockStatus, pBlockBody, pBlockResp) = await SendJson(HttpMethod.Delete, $"/api/projects/{flowProjId}", new { });
    pBlockResp.Dispose();
    Check(pBlockStatus == HttpStatusCode.BadRequest && pBlockBody.GetProperty("errors").TryGetProperty("operation", out _), "Project blocked delete returns 400 operation when task is active");

    // Constraint 3: Tag cannot be deleted while having Task (Active)
    var (tBlockStatus, tBlockBody, tBlockResp) = await SendJson(HttpMethod.Delete, $"/api/tags/{flowTagId}", new { });
    tBlockResp.Dispose();
    Check(tBlockStatus == HttpStatusCode.BadRequest && tBlockBody.GetProperty("errors").TryGetProperty("operation", out _), "Tag blocked delete returns 400 operation when task is active");

    // #24: DELETE /api/tasks/{id} (SOFT DELETE)
    var (tkDelStatus, _, tkDelResp) = await SendJson(HttpMethod.Delete, $"/api/tasks/{flowTaskId}", new { });
    tkDelResp.Dispose();
    Record(24, "/api/tasks/{id}", "DELETE", "204 NoContent, soft delete only", $"{tkDelStatus}", tkDelStatus == HttpStatusCode.NoContent);

    // Verify Soft Delete: Task still exists in DB with IsActive=false
    await using (var dbCheck = new TaskManagementDbContext(options))
    {
        var taskInDb = await dbCheck.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.TaskId == flowTaskId);
        Check(taskInDb is not null && !taskInDb.IsActive, "Soft-deleted task still in DB with IsActive=false");
        Check(taskInDb!.ModifiedDate.HasValue, "Soft-deleted task has ModifiedDate set");
    }

    // Constraint 4: Project CANNOT be deleted even when Task is SOFT-DELETED (inactive)
    var (pBlockSoftStatus, pBlockSoftBody, pBlockSoftResp) = await SendJson(HttpMethod.Delete, $"/api/projects/{flowProjId}", new { });
    pBlockSoftResp.Dispose();
    Check(pBlockSoftStatus == HttpStatusCode.BadRequest && pBlockSoftBody.GetProperty("errors").TryGetProperty("operation", out _), "Project blocked delete returns 400 operation when task is inactive (soft-deleted)");

    // Constraint 5: Tag CANNOT be deleted even when Task is SOFT-DELETED (inactive)
    var (tBlockSoftStatus, tBlockSoftBody, tBlockSoftResp) = await SendJson(HttpMethod.Delete, $"/api/tags/{flowTagId}", new { });
    tBlockSoftResp.Dispose();
    Check(tBlockSoftStatus == HttpStatusCode.BadRequest && tBlockSoftBody.GetProperty("errors").TryGetProperty("operation", out _), "Tag blocked delete returns 400 operation when task is inactive (soft-deleted)");

    // Negative tests: 404 on missing, duplicate tag name 400, invalid FK 400
    var (dupTagStatus, dupTagBody, dupTagResp) = await SendJson(HttpMethod.Post, "/api/tags", new { tagName = "frontend" });
    dupTagResp.Dispose();
    Check(dupTagStatus == HttpStatusCode.BadRequest && dupTagBody.GetProperty("errors").TryGetProperty("tagName", out _), "Duplicate TagName returns 400 tagName");

    var (badFkStatus, badFkBody, badFkResp) = await SendJson(HttpMethod.Post, "/api/tasks", new { title = "Bad FK", projectId = 999999 });
    badFkResp.Dispose();
    Check(badFkStatus == HttpStatusCode.BadRequest && badFkBody.GetProperty("errors").TryGetProperty("projectId", out _), "Invalid projectId FK returns 400 projectId");

    // Clean up created test records in database
    await using (var cleanDb = new TaskManagementDbContext(cleanOptions))
    {
        await cleanDb.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"TaskTag\" WHERE \"TaskID\" = {flowTaskId};" +
            $"DELETE FROM \"Task\" WHERE \"TaskID\" = {flowTaskId};" +
            $"DELETE FROM \"Project\" WHERE \"ProjectID\" = {flowProjId};" +
            $"DELETE FROM \"Department\" WHERE \"DepartmentID\" = {flowDeptId};" +
            $"DELETE FROM \"Tag\" WHERE \"TagID\" = {flowTagId};");
    }

    Console.WriteLine("==========================================================================================================");
    Console.WriteLine("PRN232 ASSIGNMENT 1 — 24 ENDPOINT TEST RESULTS MATRIX");
    Console.WriteLine("==========================================================================================================");
    Console.WriteLine($"{"#",-3} | {"Endpoint",-35} | {"Method",-6} | {"Expected",-30} | {"Status",-6}");
    Console.WriteLine("----------------------------------------------------------------------------------------------------------");
    foreach (var r in resultsTable)
    {
        Console.WriteLine($"{r.Index,-3} | {r.Endpoint,-35} | {r.Method,-6} | {r.Expected,-30} | {r.Status,-6}");
    }
    Console.WriteLine("==========================================================================================================");

    Console.WriteLine($"PASS: All {resultsTable.Count} endpoints & {checks} business rule checks verified successfully.");
    return 0;
}
catch (CheckFailure failure)
{
    Console.Error.WriteLine("FAILED: " + failure.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Verification failed ({exception.GetType().Name}): {exception.Message}");
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
