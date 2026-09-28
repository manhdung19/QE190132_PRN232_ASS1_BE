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
    // 0. Cleanup any residual test data before starting
    var cleanOptions = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(rawConnectionString).Options;
    await using (var cleanDb = new TaskManagementDbContext(cleanOptions))
    {
        await cleanDb.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"TaskTag\" WHERE \"TaskID\" IN (SELECT \"TaskID\" FROM \"Task\" WHERE \"Title\" LIKE 'qa-task%');" +
            "DELETE FROM \"Task\" WHERE \"Title\" LIKE 'qa-task%';");
    }

    // 1. Direct repository checks against real PostgreSQL.
    var options = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(connection.ConnectionString).Options;
    await using (var db = new TaskManagementDbContext(options))
    {
        var readOnly = await db.Database.SqlQueryRaw<string>("SELECT current_setting('default_transaction_read_only') AS \"Value\"").SingleAsync();
        Check(readOnly == "on", "Read-only PostgreSQL session for baseline checks");

        var repository = new TaskRepository(db);
        var activeTasks = await repository.GetActiveAsync();
        Check(activeTasks.Count == 17, "Seed active task count is 17");
        Check(activeTasks.Select(t => t.TaskId).SequenceEqual(Enumerable.Range(1, 17)), "Tasks ordered by TaskId 1..17");

        var task1 = await repository.GetActiveDetailAsync(1);
        Check(task1 is not null, "Task 1 active detail found");
        Check(task1!.Title == "Redesign landing page hero section", "Task 1 title matches");
        Check(task1.Status == 2, "Task 1 status is 2 (Done)");
        Check(task1.Priority == 2, "Task 1 priority is 2 (High)");
        Check(task1.DueDate == new DateOnly(2024, 2, 28), "Task 1 due date matches");
        Check(task1.ProjectId == 1, "Task 1 projectId is 1");
        Check(task1.IsActive, "Task 1 isActive is true");
        Check(task1.Tags.Select(t => t.TagId).OrderBy(id => id).SequenceEqual([1, 4]), "Task 1 has tags [1, 4]");

        var proj1Tasks = await repository.GetActiveByProjectAsync(1);
        Check(proj1Tasks.Count == 4, "Project 1 has 4 tasks in repository query");

        Check(!db.ChangeTracker.Entries().Any(), "Read queries do not track entities");
    }

    // 2. Start API host.
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

    // 3. Task 011: GET /api/tasks (list)
    var all = await Get("/api/tasks");
    Check(all.GetArrayLength() == 17, "GET /api/tasks returns exactly 17 active tasks");
    Check(all.EnumerateArray().Select(x => x.GetProperty("taskId").GetInt32()).SequenceEqual(Enumerable.Range(1, 17)), "Tasks sorted by TaskId 1..17");
    Check(all.EnumerateArray().All(x => x.GetProperty("isActive").GetBoolean()), "All tasks in list have isActive=true");

    // Task 011: GET /api/tasks/{id}
    var task1Detail = await Get("/api/tasks/1");
    Check(task1Detail.GetProperty("taskId").GetInt32() == 1, "Task 1 taskId matches");
    Check(task1Detail.GetProperty("title").GetString() == "Redesign landing page hero section", "Task 1 title matches");
    Check(task1Detail.GetProperty("status").GetInt32() == 2, "Task 1 status is 2 (Done)");
    Check(task1Detail.GetProperty("priority").GetInt32() == 2, "Task 1 priority is 2 (High)");
    Check(task1Detail.GetProperty("dueDate").GetString() == "2024-02-28", "Task 1 dueDate is 2024-02-28");
    Check(task1Detail.GetProperty("projectId").GetInt32() == 1, "Task 1 projectId is 1");
    Check(task1Detail.GetProperty("tags").GetArrayLength() == 2, "Task 1 has 2 tags");
    Check(task1Detail.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tagId").GetInt32()).SequenceEqual([1, 4]), "Task 1 tags are [1, 4]");

    // GET /api/tasks/{id} non-existent
    foreach (var missingId in new[] { 999999, 0, -1 })
    {
        var missingTask = await Get($"/api/tasks/{missingId}", HttpStatusCode.NotFound);
        Check(missingTask.GetProperty("status").GetInt32() == 404, $"Task {missingId} returns 404");
    }

    // Task 011: GET /api/tasks/project/{projectId}
    var proj1TasksApi = await Get("/api/tasks/project/1");
    Check(proj1TasksApi.GetArrayLength() == 4, "Project 1 has 4 tasks");
    Check(proj1TasksApi.EnumerateArray().Select(x => x.GetProperty("taskId").GetInt32()).SequenceEqual([1, 2, 3, 4]), "Project 1 tasks are [1, 2, 3, 4]");

    var proj2TasksApi = await Get("/api/tasks/project/2");
    Check(proj2TasksApi.GetArrayLength() == 3, "Project 2 has 3 tasks [5, 6, 7]");

    var proj6TasksApi = await Get("/api/tasks/project/6");
    Check(proj6TasksApi.GetArrayLength() == 3, "Project 6 has 3 tasks [15, 16, 17]");

    foreach (var missingProjId in new[] { 7, 999999, 0, -1 })
    {
        var missingProj = await Get($"/api/tasks/project/{missingProjId}", HttpStatusCode.NotFound);
        Check(missingProj.GetProperty("status").GetInt32() == 404, $"Inactive/missing Project {missingProjId} returns 404");
    }

    // 4. Task 015: GET /api/tasks/search
    foreach (var suffix in new[] { "", "?title=", "?title=%20%20" })
    {
        var found = await Get("/api/tasks/search" + suffix);
        Check(found.GetRawText() == all.GetRawText(), "Empty search equals active list");
    }

    var partialTitle = await Get("/api/tasks/search?title=%20hErO%20");
    Check(partialTitle.GetArrayLength() == 1 && partialTitle[0].GetProperty("taskId").GetInt32() == 1, "Case-insensitive trimmed substring search for title");

    var status2Tasks = await Get("/api/tasks/search?status=2");
    Check(status2Tasks.GetArrayLength() > 0 && status2Tasks.EnumerateArray().All(x => x.GetProperty("status").GetInt32() == 2), "Status filter matches");

    var priority3Tasks = await Get("/api/tasks/search?priority=3");
    Check(priority3Tasks.GetArrayLength() > 0 && priority3Tasks.EnumerateArray().All(x => x.GetProperty("priority").GetInt32() == 3), "Priority filter matches");

    var projFilter = await Get("/api/tasks/search?projectId=1");
    Check(projFilter.GetArrayLength() == 4, "Search by projectId matches 4 tasks");

    var tag1Filter = await Get("/api/tasks/search?tagId=1");
    Check(tag1Filter.GetArrayLength() > 0 && tag1Filter.EnumerateArray().All(x => x.GetProperty("tags").EnumerateArray().Any(t => t.GetProperty("tagId").GetInt32() == 1)), "Search by tagId=1 matches");

    var combined = await Get("/api/tasks/search?projectId=1&status=2");
    Check(combined.GetArrayLength() == 1 && combined[0].GetProperty("taskId").GetInt32() == 1, "Combined projectId=1 and status=2 matches Task 1");

    var noMatch = await Get("/api/tasks/search?projectId=1&status=2&priority=0");
    Check(noMatch.GetArrayLength() == 0, "No match returns empty array []");

    foreach (var badQuery in new[] { "?status=4", "?priority=9", "?projectId=0", "?projectId=-1", "?tagId=0", "?tagId=-1" })
    {
        var badSearch = await Get("/api/tasks/search" + badQuery, HttpStatusCode.BadRequest);
        Check(badSearch.GetProperty("errors").EnumerateObject().Any(), $"Invalid query param {badQuery} returns 400 validation error");
    }

    // 5. Task 012: POST /api/tasks Validation
    foreach (var (expectedField, badPayload) in new[]
    {
        ("title", (object)new { title = "", projectId = 1 }),
        ("title", new { title = "   ", projectId = 1 }),
        ("title", new { title = new string('A', 301), projectId = 1 }),
        ("status", new { title = "Valid", projectId = 1, status = 4 }),
        ("priority", new { title = "Valid", projectId = 1, priority = 9 }),
        ("projectId", new { title = "Valid", projectId = 0 }),
        ("projectId", new { title = "Valid", projectId = -1 }),
        ("projectId", new { title = "Valid", projectId = 999999 }),
        ("projectId", new { title = "Valid", projectId = 7 }), // inactive project
        ("tagIds", new { title = "Valid", projectId = 1, tagIds = new[] { -1 } }),
        ("tagIds", new { title = "Valid", projectId = 1, tagIds = new[] { 0 } }),
        ("tagIds", new { title = "Valid", projectId = 1, tagIds = new[] { 999999 } }),
        ("tagIds", new { title = "Valid", projectId = 1, tagIds = new[] { 1, 999999 } })
    })
    {
        var (status, body, resp) = await SendJson(HttpMethod.Post, "/api/tasks", badPayload);
        resp.Dispose();
        Check(status == HttpStatusCode.BadRequest, $"Validation 400 on bad task payload for {expectedField}");
        Check(body.GetProperty("errors").TryGetProperty(expectedField, out _), $"Validation error contains {expectedField}");
    }

    // Task 012: POST create task without tags
    var createNoTagsPayload = new
    {
        title = "qa-task-notags",
        description = "QA test task without tags",
        status = 0,
        priority = 1,
        dueDate = "2026-10-01",
        projectId = 1
    };
    var (c1Status, c1Body, c1Resp) = await SendJson(HttpMethod.Post, "/api/tasks", createNoTagsPayload);
    Check(c1Status == HttpStatusCode.Created, "POST task without tags returns 201 Created");
    var newId1 = c1Body.GetProperty("taskId").GetInt32();
    Check(newId1 > 0, "Created task has positive ID");
    Check(c1Body.GetProperty("title").GetString() == "qa-task-notags", "Title matches");
    Check(c1Body.GetProperty("status").GetInt32() == 0, "Status is 0");
    Check(c1Body.GetProperty("priority").GetInt32() == 1, "Priority is 1");
    Check(c1Body.GetProperty("dueDate").GetString() == "2026-10-01", "DueDate matches");
    Check(c1Body.GetProperty("tags").GetArrayLength() == 0, "Tags is empty []");
    Check(c1Body.GetProperty("isActive").GetBoolean(), "IsActive is true");
    Check(c1Resp.Headers.Location is not null && c1Resp.Headers.Location.OriginalString.EndsWith($"/api/tasks/{newId1}"), "Location header points to /api/tasks/{id}");
    c1Resp.Dispose();

    // Task 012: POST create task with multiple tags & deduplication
    var createTagsPayload = new
    {
        title = "qa-task-tags",
        description = "QA test task with tags",
        status = 1,
        priority = 2,
        projectId = 1,
        tagIds = new[] { 1, 2, 1 } // Duplicate 1
    };
    var (c2Status, c2Body, c2Resp) = await SendJson(HttpMethod.Post, "/api/tasks", createTagsPayload);
    Check(c2Status == HttpStatusCode.Created, "POST task with tags returns 201 Created");
    var newId2 = c2Body.GetProperty("taskId").GetInt32();
    Check(c2Body.GetProperty("tags").GetArrayLength() == 2, "Tags deduplicated to 2 tags");
    Check(c2Body.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tagId").GetInt32()).SequenceEqual([1, 2]), "Tags are [1, 2]");
    c2Resp.Dispose();
    var getCreated2 = await Get($"/api/tasks/{newId2}");
    var dbCreatedDate2 = getCreated2.GetProperty("createdDate").GetString();

    // 6. Task 013: PUT /api/tasks/{id}
    // Update newId2: replace tags [1, 2] with [2, 3], update title, verify ModifiedDate updated and CreatedDate preserved
    var updatePayload = new
    {
        title = "qa-task-updated",
        description = "Updated description",
        status = 2,
        priority = 3,
        projectId = 1,
        tagIds = new[] { 2, 3 }
    };
    var (uStatus, uBody, uResp) = await SendJson(HttpMethod.Put, $"/api/tasks/{newId2}", updatePayload);
    uResp.Dispose();
    Check(uStatus == HttpStatusCode.OK, "PUT task returns 200 OK");
    Check(uBody.GetProperty("title").GetString() == "qa-task-updated", "Title updated");
    Check(uBody.GetProperty("status").GetInt32() == 2, "Status updated to 2");
    Check(uBody.GetProperty("priority").GetInt32() == 3, "Priority updated to 3");
    Check(uBody.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tagId").GetInt32()).SequenceEqual([2, 3]), "Tags replaced from [1, 2] to [2, 3]");
    Check(DateTime.Parse(uBody.GetProperty("createdDate").GetString()!) == DateTime.Parse(dbCreatedDate2!), "CreatedDate preserved across PUT");
    Check(uBody.GetProperty("modifiedDate").ValueKind != JsonValueKind.Null, "ModifiedDate is set upon PUT");

    // PUT with tagIds: [] removes all tags
    var clearTagsPayload = new
    {
        title = "qa-task-updated",
        status = 2,
        priority = 3,
        projectId = 1,
        tagIds = Array.Empty<int>()
    };
    var (clrStatus, clrBody, clrResp) = await SendJson(HttpMethod.Put, $"/api/tasks/{newId2}", clearTagsPayload);
    clrResp.Dispose();
    Check(clrStatus == HttpStatusCode.OK, "PUT with empty tagIds returns 200 OK");
    Check(clrBody.GetProperty("tags").GetArrayLength() == 0, "Tags cleared to empty []");

    // PUT invalid tagIds returns 400
    var (badPutStatus, badPutBody, badPutResp) = await SendJson(HttpMethod.Put, $"/api/tasks/{newId2}", new
    {
        title = "qa-task-updated",
        projectId = 1,
        tagIds = new[] { 999999 }
    });
    badPutResp.Dispose();
    Check(badPutStatus == HttpStatusCode.BadRequest && badPutBody.GetProperty("errors").TryGetProperty("tagIds", out _), "PUT bad tagIds returns 400 tagIds");

    // PUT invalid projectId returns 400
    var (badPutProjStatus, badPutProjBody, badPutProjResp) = await SendJson(HttpMethod.Put, $"/api/tasks/{newId2}", new
    {
        title = "qa-task-updated",
        projectId = 7 // inactive
    });
    badPutProjResp.Dispose();
    Check(badPutProjStatus == HttpStatusCode.BadRequest && badPutProjBody.GetProperty("errors").TryGetProperty("projectId", out _), "PUT inactive projectId returns 400 projectId");

    // PUT non-existent returns 404
    foreach (var missingId in new[] { 999999, 0, -1 })
    {
        var (pStatus, pBody, pResp) = await SendJson(HttpMethod.Put, $"/api/tasks/{missingId}", new { title = "some", projectId = 1 });
        pResp.Dispose();
        Check(pStatus == HttpStatusCode.NotFound && pBody.GetProperty("status").GetInt32() == 404, $"PUT {missingId} returns 404");
    }

    // 7. Task 014: DELETE /api/tasks/{id} (Soft delete)
    var (d1Status, _, d1Resp) = await SendJson(HttpMethod.Delete, $"/api/tasks/{newId1}", new { });
    d1Resp.Dispose();
    Check(d1Status == HttpStatusCode.NoContent, "DELETE task returns 204 NoContent");

    var (d2Status, _, d2Resp) = await SendJson(HttpMethod.Delete, $"/api/tasks/{newId2}", new { });
    d2Resp.Dispose();
    Check(d2Status == HttpStatusCode.NoContent, "DELETE task returns 204 NoContent");

    // Repeated DELETE on soft-deleted task returns 404
    var (repStatus, repBody, repResp) = await SendJson(HttpMethod.Delete, $"/api/tasks/{newId1}", new { });
    repResp.Dispose();
    Check(repStatus == HttpStatusCode.NotFound && repBody.GetProperty("status").GetInt32() == 404, "Repeated DELETE returns 404");

    // GET soft-deleted task returns 404
    var missingSoft = await Get($"/api/tasks/{newId1}", HttpStatusCode.NotFound);
    Check(missingSoft.GetProperty("status").GetInt32() == 404, "GET soft-deleted task returns 404");

    // PUT soft-deleted task returns 404
    var (pSoftStatus, pSoftBody, pSoftResp) = await SendJson(HttpMethod.Put, $"/api/tasks/{newId1}", new { title = "reactivate", projectId = 1 });
    pSoftResp.Dispose();
    Check(pSoftStatus == HttpStatusCode.NotFound && pSoftBody.GetProperty("status").GetInt32() == 404, "PUT soft-deleted task returns 404");

    // Verify task still exists in DB with IsActive=false
    await using (var dbCheck = new TaskManagementDbContext(options))
    {
        var inDb = await dbCheck.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.TaskId == newId1);
        Check(inDb is not null, "Soft-deleted task still exists physically in DB");
        Check(inDb!.IsActive == false, "Soft-deleted task has IsActive=false in DB");
        Check(inDb.ModifiedDate.HasValue, "Soft-deleted task has ModifiedDate set in DB");
    }

    // Verify soft-deleted task does not appear in GET /api/tasks or search
    var activeAfterSoftDel = await Get("/api/tasks");
    Check(activeAfterSoftDel.GetArrayLength() == 17, "Active list has exactly 17 tasks (soft-deleted excluded)");
    Check(activeAfterSoftDel.EnumerateArray().All(x => x.GetProperty("taskId").GetInt32() != newId1 && x.GetProperty("taskId").GetInt32() != newId2), "Soft-deleted tasks excluded from active list");

    var searchAfterSoftDel = await Get("/api/tasks/search?title=qa-task");
    Check(searchAfterSoftDel.GetArrayLength() == 0, "Soft-deleted tasks excluded from search");

    // Verify Project 1 & Tag deletion constraint remains blocked
    var (blockedProjStatus, blockedProjBody, blockedProjResp) = await SendJson(HttpMethod.Delete, "/api/projects/1", new { });
    blockedProjResp.Dispose();
    Check(blockedProjStatus == HttpStatusCode.BadRequest && blockedProjBody.GetProperty("errors").TryGetProperty("operation", out _), "DELETE Project 1 with tasks is blocked with 400 operation");

    var (blockedTagStatus, blockedTagBody, blockedTagResp) = await SendJson(HttpMethod.Delete, "/api/tags/1", new { });
    blockedTagResp.Dispose();
    Check(blockedTagStatus == HttpStatusCode.BadRequest && blockedTagBody.GetProperty("errors").TryGetProperty("operation", out _), "DELETE Tag 1 with tasks is blocked with 400 operation");

    // 8. Clean up created test tasks
    await using (var cleanDb = new TaskManagementDbContext(cleanOptions))
    {
        await cleanDb.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"TaskTag\" WHERE \"TaskID\" IN ({newId1}, {newId2});" +
            $"DELETE FROM \"Task\" WHERE \"TaskID\" IN ({newId1}, {newId2});");
    }

    var finalActive = await Get("/api/tasks");
    Check(finalActive.GetArrayLength() == 17, "Database restored: exactly 17 seed tasks");

    // 9. Route constraints on {id:int}
    using var invalidGet = await client.GetAsync("/api/tasks/not-an-int");
    Check(invalidGet.StatusCode == HttpStatusCode.NotFound, "Integer route constraint on GET tasks/{id}");

    using var invalidProjGet = await client.GetAsync("/api/tasks/project/not-an-int");
    Check(invalidProjGet.StatusCode == HttpStatusCode.NotFound, "Integer route constraint on GET tasks/project/{projectId}");

    using var invalidPut = await client.PutAsync("/api/tasks/not-an-int", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
    Check(invalidPut.StatusCode == HttpStatusCode.NotFound, "Integer route constraint on PUT tasks/{id}");

    using var invalidDelete = await client.DeleteAsync("/api/tasks/not-an-int");
    Check(invalidDelete.StatusCode == HttpStatusCode.NotFound, "Integer route constraint on DELETE tasks/{id}");

    // 10. Swagger verification
    var swagger = await Get("/swagger/v1/swagger.json");
    var paths = swagger.GetProperty("paths");
    Check(paths.GetProperty("/api/tasks").TryGetProperty("get", out _), "Swagger GET /api/tasks");
    Check(paths.GetProperty("/api/tasks").TryGetProperty("post", out _), "Swagger POST /api/tasks");
    Check(paths.GetProperty("/api/tasks/{id}").TryGetProperty("get", out _), "Swagger GET /api/tasks/{id}");
    Check(paths.GetProperty("/api/tasks/{id}").TryGetProperty("put", out _), "Swagger PUT /api/tasks/{id}");
    Check(paths.GetProperty("/api/tasks/{id}").TryGetProperty("delete", out _), "Swagger DELETE /api/tasks/{id}");
    Check(paths.GetProperty("/api/tasks/project/{projectId}").TryGetProperty("get", out _), "Swagger GET /api/tasks/project/{projectId}");
    Check(paths.GetProperty("/api/tasks/search").TryGetProperty("get", out _), "Swagger GET /api/tasks/search");

    using var ui = await client.GetAsync("/swagger/index.html");
    Check(ui.IsSuccessStatusCode && (await ui.Content.ReadAsStringAsync()).Contains("Swagger UI"), "Swagger UI served");

    Console.WriteLine($"PASS: {checks} Task checks against real PostgreSQL; CRUD, tags, atomic create/replace, soft delete, and search verified.");
    return 0;
}
catch (CheckFailure failure)
{
    Console.Error.WriteLine("FAILED: " + failure.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Task verification failed ({exception.GetType().Name}). Connection details withheld: {exception.Message}");
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
