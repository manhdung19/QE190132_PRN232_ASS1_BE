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

    // Cleanup any leftovers from previous failed test runs
    var writableOptions = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(rawConnectionString).Options;
    await using (var cleanDb = new TaskManagementDbContext(writableOptions))
    {
        var tempTags = await cleanDb.Tags
            .Where(t => t.TagName.StartsWith("qa-") || t.TagName.StartsWith("test-"))
            .ToListAsync();
        if (tempTags.Count > 0)
        {
            cleanDb.Tags.RemoveRange(tempTags);
            await cleanDb.SaveChangesAsync();
        }
    }

    // Direct repository checks against real PostgreSQL.
    var options = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(connection.ConnectionString).Options;
    await using (var db = new TaskManagementDbContext(options))
    {
        var readOnly = await db.Database.SqlQueryRaw<string>("SELECT current_setting('default_transaction_read_only') AS \"Value\"").SingleAsync();
        Check(readOnly == "on", "Read-only PostgreSQL session for baseline checks");
        var repository = new TagRepository(db);
        var tags = await repository.GetAllAsync();
        Check(tags.Count == 10, "Seed tag count is 10");
        Check(tags.Select(t => t.TagId).SequenceEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]), "Tags ordered by TagId");
        Check(await repository.HasTasksAsync(1), "Tag 1 has tasks");
        Check(!await repository.HasTasksAsync(6), "Tag 6 has no tasks");
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

    // 1. GET /api/tags: 10 tags ordered
    var all = await Get("/api/tags");
    Check(all.GetArrayLength() == 10, "HTTP 10 seed tags");
    Check(all.EnumerateArray().Select(x => x.GetProperty("tagId").GetInt32()).SequenceEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]), "Tags ordered by tagId");
    Check(all.EnumerateArray().All(x => !string.IsNullOrWhiteSpace(x.GetProperty("tagName").GetString())
        && !x.TryGetProperty("tasks", out _)), "TagListItem DTO shape without navigation cycles");

    // 2. Validation errors on POST and PUT
    foreach (var (badPayload, expectedField) in new (object Payload, string Field)[] {
        (new { tagName = "", color = "#123ABC" }, "tagName"),
        (new { tagName = "   ", color = "#123ABC" }, "tagName"),
        (new { tagName = new string('A', 51), color = "#123ABC" }, "tagName"),
        (new { tagName = "valid-tag", color = "red" }, "color"),
        (new { tagName = "valid-tag", color = "#123" }, "color"),
        (new { tagName = "valid-tag", color = "#1234567" }, "color"),
    })
    {
        var (status, body, resp) = await SendJson(HttpMethod.Post, "/api/tags", badPayload);
        resp.Dispose();
        Check(status == HttpStatusCode.BadRequest, $"Validation 400 on bad tag payload for {expectedField}");
        Check(body.GetProperty("errors").TryGetProperty(expectedField, out _), $"Validation error contains {expectedField}");
    }

    // 3. Unique TagName validation
    var duplicateCreate = new { tagName = "frontend", color = "#123ABC" };
    var (dupStatus, dupBody, dupResp) = await SendJson(HttpMethod.Post, "/api/tags", duplicateCreate);
    dupResp.Dispose();
    Check(dupStatus == HttpStatusCode.BadRequest, "POST duplicate tagName returns 400");
    Check(dupBody.GetProperty("errors").TryGetProperty("tagName", out _), "Duplicate tagName error maps to tagName field");

    // 4. Complete CRUD Lifecycle on temporary tags
    // Create tag with color
    var createPayload = new { tagName = "qa-temporary", color = "#AABBCC" };
    var (createStatus, createBody, createResp) = await SendJson(HttpMethod.Post, "/api/tags", createPayload);
    Check(createStatus == HttpStatusCode.Created, "POST /api/tags returns 201 Created");
    var newId = createBody.GetProperty("tagId").GetInt32();
    Check(newId > 0, "Created tag has positive ID");
    Check(createBody.GetProperty("tagName").GetString() == "qa-temporary", "Created tagName matches");
    Check(createBody.GetProperty("color").GetString() == "#AABBCC", "Created color matches");
    Check(createResp.Headers.Location is not null && createResp.Headers.Location.OriginalString.EndsWith($"/api/tags/{newId}"), "Location header points to /api/tags/{id}");
    createResp.Dispose();

    // Create tag with null color (Color is nullable)
    var noColorPayload = new { tagName = "qa-nocolor", color = (string?)null };
    var (noColorStatus, noColorBody, noColorResp) = await SendJson(HttpMethod.Post, "/api/tags", noColorPayload);
    noColorResp.Dispose();
    Check(noColorStatus == HttpStatusCode.Created, "POST tag with null color returns 201 Created");
    var noColorId = noColorBody.GetProperty("tagId").GetInt32();
    Check(noColorBody.GetProperty("color").ValueKind == JsonValueKind.Null, "Color is null");

    // PUT: update keeping the same tagName succeeds
    var updateSameName = new { tagName = "qa-temporary", color = "#112233" };
    var (sameStatus, sameBody, sameResp) = await SendJson(HttpMethod.Put, $"/api/tags/{newId}", updateSameName);
    sameResp.Dispose();
    Check(sameStatus == HttpStatusCode.OK, "PUT tag with same tagName succeeds");
    Check(sameBody.GetProperty("color").GetString() == "#112233", "Updated color matches");

    // PUT: update renaming to new unique name succeeds
    var updateNewName = new { tagName = "qa-renamed", color = "#112233" };
    var (newNameStatus, newNameBody, newNameResp) = await SendJson(HttpMethod.Put, $"/api/tags/{newId}", updateNewName);
    newNameResp.Dispose();
    Check(newNameStatus == HttpStatusCode.OK, "PUT tag with new unique tagName succeeds");
    Check(newNameBody.GetProperty("tagName").GetString() == "qa-renamed", "Updated tagName matches");

    // PUT: update renaming to an existing tag's name returns 400
    var updateDuplicate = new { tagName = "backend", color = "#112233" };
    var (dupUpdStatus, dupUpdBody, dupUpdResp) = await SendJson(HttpMethod.Put, $"/api/tags/{newId}", updateDuplicate);
    dupUpdResp.Dispose();
    Check(dupUpdStatus == HttpStatusCode.BadRequest, "PUT duplicate tagName returns 400");
    Check(dupUpdBody.GetProperty("errors").TryGetProperty("tagName", out _), "Duplicate error maps to tagName");

    // PUT non-existent returns 404
    foreach (var missingId in new[] { 999999, 0, -1 })
    {
        var (pStatus, pBody, pResp) = await SendJson(HttpMethod.Put, $"/api/tags/{missingId}",
            new { tagName = "some-name", color = "#112233" });
        pResp.Dispose();
        Check(pStatus == HttpStatusCode.NotFound && pBody.GetProperty("status").GetInt32() == 404, $"PUT {missingId} returns 404");
    }

    // 5. Task 010: Blocked DELETE when tag is used by any Task
    var (delBlockedStatus, delBlockedBody, delBlockedResp) = await SendJson(HttpMethod.Delete, "/api/tags/1");
    delBlockedResp.Dispose();
    Check(delBlockedStatus == HttpStatusCode.BadRequest, "DELETE tag with associated tasks returns 400");
    Check(delBlockedBody.GetProperty("errors").TryGetProperty("operation", out _), "Blocked delete error maps to operation");

    // 6. Task 010: Delete tags that have no tasks
    var (del1Status, _, del1Resp) = await SendJson(HttpMethod.Delete, $"/api/tags/{newId}");
    del1Resp.Dispose();
    Check(del1Status == HttpStatusCode.NoContent, "DELETE temporary tag returns 204");

    var (del2Status, _, del2Resp) = await SendJson(HttpMethod.Delete, $"/api/tags/{noColorId}");
    del2Resp.Dispose();
    Check(del2Status == HttpStatusCode.NoContent, "DELETE temporary no-color tag returns 204");

    // DELETE non-existent returns 404
    foreach (var missingId in new[] { newId, 999999, 0, -1 })
    {
        var (dStatus, dBody, dResp) = await SendJson(HttpMethod.Delete, $"/api/tags/{missingId}");
        dResp.Dispose();
        Check(dStatus == HttpStatusCode.NotFound && dBody.GetProperty("status").GetInt32() == 404, $"DELETE {missingId} returns 404");
    }

    // Verify active count is restored to exactly 10
    var finalAll = await Get("/api/tags");
    Check(finalAll.GetArrayLength() == 10, "Database restored: exactly 10 seed tags");

    // Route constraint on PUT and DELETE {id:int}
    using var invalidPut = await client.PutAsync("/api/tags/not-an-int", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
    Check(invalidPut.StatusCode == HttpStatusCode.NotFound, $"Integer route constraint on PUT tags/{{id}}, got {invalidPut.StatusCode}");

    using var invalidDelete = await client.DeleteAsync("/api/tags/not-an-int");
    Check(invalidDelete.StatusCode == HttpStatusCode.NotFound, $"Integer route constraint on DELETE tags/{{id}}, got {invalidDelete.StatusCode}");


    // Swagger verification
    var swagger = await Get("/swagger/v1/swagger.json");
    var paths = swagger.GetProperty("paths");
    Check(paths.GetProperty("/api/tags").TryGetProperty("get", out _), "Swagger GET /api/tags");
    Check(paths.GetProperty("/api/tags").TryGetProperty("post", out _), "Swagger POST /api/tags");
    Check(paths.GetProperty("/api/tags/{id}").TryGetProperty("put", out _), "Swagger PUT /api/tags/{id}");
    Check(paths.GetProperty("/api/tags/{id}").TryGetProperty("delete", out _), "Swagger DELETE /api/tags/{id}");
    using var ui = await client.GetAsync("/swagger/index.html");
    Check(ui.IsSuccessStatusCode && (await ui.Content.ReadAsStringAsync()).Contains("Swagger UI"), "Swagger UI served");

    Console.WriteLine($"PASS: {checks} Tag checks against real PostgreSQL; CRUD, validation, duplicate check, and blocked delete verified.");
    return 0;
}
catch (CheckFailure failure)
{
    Console.Error.WriteLine("FAILED: " + failure.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Tag verification failed ({exception.GetType().Name}). Connection details withheld: {exception.Message}");
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
