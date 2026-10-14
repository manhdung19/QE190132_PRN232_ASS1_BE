using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskTrack.Repo.Data;
using TaskTrack.Service.Services;

var stage = args.SingleOrDefault() ?? "register";
if (stage is not ("register" or "login" or "authorization" or "accounts")) throw new ArgumentException("Unknown auth check stage.");
var connectionString = Environment.GetEnvironmentVariable("A2_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set A2_TEST_CONNECTION for a disposable local PostgreSQL database.");
var connection = new NpgsqlConnectionStringBuilder(connectionString);
if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "tasktrack_a2_test")
    throw new InvalidOperationException("This check only runs against local database tasktrack_a2_test.");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();

var apiDll = Path.GetFullPath("TaskTrack.API/bin/Debug/net8.0/TaskTrack.API.dll");
if (!File.Exists(apiDll)) throw new InvalidOperationException("Build TaskTrack.API before this check.");
var start = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    WorkingDirectory = Path.GetDirectoryName(apiDll)!
};
start.ArgumentList.Add(apiDll);
start.Environment.Remove("DATABASE_URL");
start.Environment["ConnectionStrings__TaskTrack"] = connection.ConnectionString;
start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
start.Environment["Logging__LogLevel__Default"] = "None";
start.Environment["JWT_SECRET"] = "LocalAuthCheckSecretOnly_12345678901234567890";

using var api = Process.Start(start) ?? throw new InvalidOperationException("API process did not start.");
api.OutputDataReceived += (_, _) => { };
api.ErrorDataReceived += (_, _) => { };
api.BeginOutputReadLine();
api.BeginErrorReadLine();
using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

try
{
    var ready = false;
    for (var attempt = 0; attempt < 40; attempt++)
    {
        if (api.HasExited) throw new InvalidOperationException("API process exited before health check.");
        try
        {
            using var response = await client.GetAsync("/api/health");
            if (response.IsSuccessStatusCode) { ready = true; break; }
        }
        catch (HttpRequestException) { }
        await Task.Delay(250);
    }
    Check(ready, "API did not become ready.");

    const string password = "ExamplePass123!";
    using var registered = await client.PostAsJsonAsync("/api/auth/register", new
    {
        fullName = " Test Staff ",
        email = "  STAFF@EXAMPLE.INVALID  ",
        password,
        role = 1
    });
    Check(registered.StatusCode == HttpStatusCode.Created, "Register did not return 201.");
    using var body = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
    var account = body.RootElement;
    var id = account.GetProperty("accountId").GetInt32();
    Check(id > 0 && account.GetProperty("role").GetInt32() == 0, "Register did not force Staff role.");
    Check(account.GetProperty("email").GetString() == "staff@example.invalid", "Email was not normalized.");
    Check(registered.Headers.Location?.ToString() == $"/api/accounts/{id}", "Register Location is wrong.");
    Check(!account.TryGetProperty("passwordHash", out _) && !account.TryGetProperty("password", out _),
        "Register response exposed password data.");

    var options = new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql(connection.ConnectionString).Options;
    await using (var db = new TaskManagementDbContext(options))
    {
        var stored = await db.SystemAccounts.AsNoTracking().SingleAsync(x => x.AccountId == id);
        Check(stored.Role == 0 && stored.FullName == "Test Staff", "Stored account data is wrong.");
        Check(stored.PasswordHash != password && stored.PasswordHash.StartsWith("pbkdf2-sha256$"),
            "Password was not stored as PBKDF2 hash.");
        Check(PasswordHasher.Verify(password, stored.PasswordHash), "Correct password failed verification.");
        Check(!PasswordHasher.Verify("WrongPass123!", stored.PasswordHash), "Incorrect password verified.");
    }

    using var duplicate = await client.PostAsJsonAsync("/api/auth/register",
        new { fullName = "Duplicate", email = "staff@example.invalid", password });
    Check(duplicate.StatusCode == HttpStatusCode.Conflict, "Duplicate email did not return 409.");

    using var invalid = await client.PostAsJsonAsync("/api/auth/register",
        new { fullName = " ", email = "invalid", password = "short" });
    Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid registration did not return 400.");

    var race = await Task.WhenAll(
        client.PostAsJsonAsync("/api/auth/register",
            new { fullName = "Race A", email = "race@example.invalid", password }),
        client.PostAsJsonAsync("/api/auth/register",
            new { fullName = "Race B", email = "race@example.invalid", password }));
    using (race[0])
    using (race[1])
    {
        Check(race.Count(x => x.StatusCode == HttpStatusCode.Created) == 1 &&
              race.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1,
            "Concurrent duplicate registrations did not return one 201 and one 409.");
    }

    Console.WriteLine("PASS A2-04: 201 Staff, normalized email, salted PBKDF2, no secret response, 400, 409 and concurrent duplicate 409.");
    if (stage is "login" or "authorization" or "accounts")
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = " STAFF@EXAMPLE.INVALID ", password });
        Check(login.StatusCode == HttpStatusCode.OK, $"Valid login did not return 200: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var root = loginBody.RootElement;
        Check(root.GetProperty("tokenType").GetString() == "Bearer", "Token type is wrong.");
        Check(root.GetProperty("account").GetProperty("accountId").GetInt32() == id,
            "Login account is wrong.");
        Check(!root.GetProperty("account").TryGetProperty("passwordHash", out _),
            "Login exposed the password hash.");
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler { MapInboundClaims = false };
        var tokenText = root.GetProperty("token").GetString()!;
        var principal = jwt.ValidateToken(tokenText, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false, ValidateAudience = false, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes("LocalAuthCheckSecretOnly_12345678901234567890")),
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero
        }, out _);
        Check(principal.FindFirst("accountId")?.Value == id.ToString() &&
              principal.FindFirst("email")?.Value == "staff@example.invalid" &&
              principal.FindFirst("role")?.Value == "Staff" &&
              principal.FindFirst("roleId")?.Value == "0", "JWT claims are wrong.");
        var expires = root.GetProperty("expiresAt").GetDateTime();
        Check(expires > DateTime.UtcNow.AddHours(23) && expires < DateTime.UtcNow.AddHours(25),
            "JWT expiry is not 24 hours.");
        using var wrongPassword = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "staff@example.invalid", password = "WrongPass123!" });
        using var wrongEmail = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "unknown@example.invalid", password });
        Check(wrongPassword.StatusCode == HttpStatusCode.Unauthorized &&
              wrongEmail.StatusCode == HttpStatusCode.Unauthorized &&
              await wrongPassword.Content.ReadAsStringAsync() == await wrongEmail.Content.ReadAsStringAsync(),
            "Invalid credentials must return the same 401 response.");
        Console.WriteLine("PASS A2-05: signed JWT claims/expiry, safe response and generic 401.");
        if (stage == "accounts")
        {
            using var anonymous = await client.GetAsync("/api/accounts");
            Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Anonymous account list was not 401.");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenText);
            using var staffList = await client.GetAsync("/api/accounts");
            using var staffUpdate = await client.PutAsJsonAsync($"/api/accounts/{id}", new { fullName = "Wrong" });
            using var staffDelete = await client.DeleteAsync($"/api/accounts/{id}");
            Check(staffList.StatusCode == HttpStatusCode.Forbidden &&
                  staffUpdate.StatusCode == HttpStatusCode.Forbidden &&
                  staffDelete.StatusCode == HttpStatusCode.Forbidden, "Staff account API was not 403.");

            await using (var db = new TaskManagementDbContext(options))
            {
                var firstAdmin = await db.SystemAccounts.SingleAsync(x => x.AccountId == id);
                firstAdmin.Role = 1;
                await db.SaveChangesAsync();
            }
            client.DefaultRequestHeaders.Authorization = null;
            using var adminLogin = await client.PostAsJsonAsync("/api/auth/login",
                new { email = "staff@example.invalid", password });
            Check(adminLogin.StatusCode == HttpStatusCode.OK, "Admin login failed.");
            using var adminLoginBody = JsonDocument.Parse(await adminLogin.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", adminLoginBody.RootElement.GetProperty("token").GetString());
            using var list = await client.GetAsync("/api/accounts");
            Check(list.StatusCode == HttpStatusCode.OK, "Admin list failed.");
            using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Check(listBody.RootElement.GetArrayLength() >= 2 &&
                  !listBody.RootElement[0].TryGetProperty("passwordHash", out _), "Account list exposed hash.");
            using var detail = await client.GetAsync($"/api/accounts/{id}");
            Check(detail.StatusCode == HttpStatusCode.OK, "Admin detail failed.");
            using var missing = await client.GetAsync("/api/accounts/999999");
            Check(missing.StatusCode == HttpStatusCode.NotFound, "Missing account did not return 404.");
            using var invalidUpdate = await client.PutAsJsonAsync($"/api/accounts/{id}", new { role = 5 });
            using var emptyUpdate = await client.PutAsJsonAsync($"/api/accounts/{id}", new { });
            using var nullUpdate = await client.PutAsJsonAsync($"/api/accounts/{id}", new { fullName = (string?)null });
            Check(invalidUpdate.StatusCode == HttpStatusCode.BadRequest &&
                  emptyUpdate.StatusCode == HttpStatusCode.BadRequest &&
                  nullUpdate.StatusCode == HttpStatusCode.BadRequest, "Invalid account updates were accepted.");
            using var lastAdminDemote = await client.PutAsJsonAsync($"/api/accounts/{id}", new { role = 0 });
            using var selfDelete = await client.DeleteAsync($"/api/accounts/{id}");
            Check(lastAdminDemote.StatusCode == HttpStatusCode.BadRequest &&
                  selfDelete.StatusCode == HttpStatusCode.BadRequest, "Last/self Admin protection failed.");

            using var newStaff = await client.PostAsJsonAsync("/api/auth/register",
                new { fullName = "Manage Staff", email = "manage@example.invalid", password });
            Check(newStaff.StatusCode == HttpStatusCode.Created, "Second Staff registration failed.");
            using var newStaffBody = JsonDocument.Parse(await newStaff.Content.ReadAsStringAsync());
            var managedId = newStaffBody.RootElement.GetProperty("accountId").GetInt32();
            using var updated = await client.PutAsJsonAsync($"/api/accounts/{managedId}",
                new { fullName = "Managed Staff", role = 1 });
            Check(updated.StatusCode == HttpStatusCode.OK, "Admin update failed.");
            using var updatedBody = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
            Check(updatedBody.RootElement.GetProperty("fullName").GetString() == "Managed Staff" &&
                  updatedBody.RootElement.GetProperty("role").GetInt32() == 1 &&
                  !updatedBody.RootElement.TryGetProperty("passwordHash", out _), "Account update response is wrong.");

            using var createdTask = await client.PostAsJsonAsync("/api/tasks",
                new { title = "Account ownership lock", projectId = 1, status = 0, priority = 1 });
            Check(createdTask.StatusCode == HttpStatusCode.Created, "Admin Task creation failed.");
            using var createdTaskBody = JsonDocument.Parse(await createdTask.Content.ReadAsStringAsync());
            var taskId = createdTaskBody.RootElement.GetProperty("taskId").GetInt32();
            using var softDelete = await client.DeleteAsync($"/api/tasks/{taskId}");
            Check(softDelete.StatusCode == HttpStatusCode.NoContent, "Task soft delete failed.");
            using var blockOwner = await client.DeleteAsync($"/api/accounts/{id}");
            Check(blockOwner.StatusCode == HttpStatusCode.BadRequest, "Account with inactive Task could be deleted.");

            using var thirdStaff = await client.PostAsJsonAsync("/api/auth/register",
                new { fullName = "Delete Me", email = "delete@example.invalid", password });
            using var thirdStaffBody = JsonDocument.Parse(await thirdStaff.Content.ReadAsStringAsync());
            var deleteId = thirdStaffBody.RootElement.GetProperty("accountId").GetInt32();
            using var deleted = await client.DeleteAsync($"/api/accounts/{deleteId}");
            Check(deleted.StatusCode == HttpStatusCode.NoContent, "Admin delete failed.");
            using var deletedDetail = await client.GetAsync($"/api/accounts/{deleteId}");
            Check(deletedDetail.StatusCode == HttpStatusCode.NotFound, "Deleted account still exists.");
            Console.WriteLine("PASS A2-07: 401/403, Admin list/detail/update/delete, validation/404, last Admin and Task ownership, no hash.");
        }
        if (stage == "authorization")
        {
            foreach (var resource in new[] { "departments", "projects", "tasks", "tags" })
            {
                using var publicGet = await client.GetAsync($"/api/{resource}");
                Check(publicGet.StatusCode == HttpStatusCode.OK, $"Anonymous GET {resource} failed.");
                foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
                {
                    var path = method == HttpMethod.Post ? $"/api/{resource}" : $"/api/{resource}/1";
                    using var message = new HttpRequestMessage(method, path);
                    if (method != HttpMethod.Delete) message.Content = JsonContent.Create(new { });
                    using var denied = await client.SendAsync(message);
                    Check(denied.StatusCode == HttpStatusCode.Unauthorized,
                        $"Anonymous {method} {resource} did not return 401.");
                }
            }

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "bad.token.value");
            using (var bad = await client.PostAsJsonAsync("/api/tags", new { tagName = "Bad Token" }))
                Check(bad.StatusCode == HttpStatusCode.Unauthorized, "Bad token was accepted.");

            var secretKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes("LocalAuthCheckSecretOnly_12345678901234567890"));
            var expiredJwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                claims: [new System.Security.Claims.Claim("accountId", id.ToString()),
                    new System.Security.Claims.Claim("role", "Staff")],
                notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddHours(-1),
                signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    secretKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", jwt.WriteToken(expiredJwt));
            using (var expired = await client.PostAsJsonAsync("/api/tags", new { tagName = "Expired Token" }))
                Check(expired.StatusCode == HttpStatusCode.Unauthorized, "Expired token was accepted.");

            var unsupportedRoleJwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                claims: [new System.Security.Claims.Claim("accountId", id.ToString()),
                    new System.Security.Claims.Claim("role", "Unknown")],
                notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    secretKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", jwt.WriteToken(unsupportedRoleJwt));
            using (var forbidden = await client.PostAsJsonAsync("/api/tags", new { tagName = "Unknown Role" }))
                Check(forbidden.StatusCode == HttpStatusCode.Forbidden, "Unsupported role was accepted.");

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenText);
            using var tag = await client.PostAsJsonAsync("/api/tags", new { tagName = "A2 Auth Tag" });
            Check(tag.StatusCode == HttpStatusCode.Created, "Staff cannot create Tag.");
            using var tagBody = JsonDocument.Parse(await tag.Content.ReadAsStringAsync());
            var tagId = tagBody.RootElement.GetProperty("tagId").GetInt32();
            using var updateTag = await client.PutAsJsonAsync($"/api/tags/{tagId}", new { tagName = "A2 Auth Tag Updated" });
            Check(updateTag.StatusCode == HttpStatusCode.OK, "Staff cannot update Tag.");
            using var deleteTag = await client.DeleteAsync($"/api/tags/{tagId}");
            Check(deleteTag.StatusCode == HttpStatusCode.NoContent, "Staff cannot delete Tag.");
            using var department = await client.PostAsJsonAsync("/api/departments",
                new { departmentName = "A2 Auth Department", departmentDescription = "Authorization check", isActive = true });
            Check(department.StatusCode == HttpStatusCode.Created, "Staff cannot create Department.");
            using var departmentBody = JsonDocument.Parse(await department.Content.ReadAsStringAsync());
            var departmentId = departmentBody.RootElement.GetProperty("departmentId").GetInt32();
            using var project = await client.PostAsJsonAsync("/api/projects",
                new { projectName = "A2 Auth Project", startDate = "2026-10-14", departmentId, status = 0, isActive = true });
            Check(project.StatusCode == HttpStatusCode.Created, "Staff cannot create Project.");
            using var projectBody = JsonDocument.Parse(await project.Content.ReadAsStringAsync());
            var projectId = projectBody.RootElement.GetProperty("projectId").GetInt32();
            using var task = await client.PostAsJsonAsync("/api/tasks",
                new { title = "A2 Owned Task", projectId, status = 0, priority = 1, isActive = true, createdById = 999999 });
            Check(task.StatusCode == HttpStatusCode.Created, "Staff cannot create Task.");
            using var taskBody = JsonDocument.Parse(await task.Content.ReadAsStringAsync());
            var taskId = taskBody.RootElement.GetProperty("taskId").GetInt32();
            await using (var db = new TaskManagementDbContext(options))
            {
                var saved = await db.Tasks.AsNoTracking().SingleAsync(x => x.TaskId == taskId);
                Check(saved.CreatedById == id, "Task ownership was not taken from JWT accountId.");
            }
            using var cors = new HttpRequestMessage(HttpMethod.Options, "/api/tasks");
            cors.Headers.Add("Origin", "http://localhost:3000");
            cors.Headers.Add("Access-Control-Request-Method", "POST");
            cors.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            using var preflight = await client.SendAsync(cors);
            Check(preflight.IsSuccessStatusCode &&
                  preflight.Headers.TryGetValues("Access-Control-Allow-Headers", out var headers) &&
                  headers.Any(x => x.Contains("authorization", StringComparison.OrdinalIgnoreCase)),
                "CORS preflight did not allow Authorization.");
            await using (var db = new TaskManagementDbContext(options))
            {
                var admin = await db.SystemAccounts.SingleAsync(x => x.AccountId == id);
                admin.Role = 1;
                await db.SaveChangesAsync();
            }
            client.DefaultRequestHeaders.Authorization = null;
            using var adminLogin = await client.PostAsJsonAsync("/api/auth/login",
                new { email = "staff@example.invalid", password });
            Check(adminLogin.StatusCode == HttpStatusCode.OK, "Admin login failed.");
            using var adminBody = JsonDocument.Parse(await adminLogin.Content.ReadAsStringAsync());
            var adminToken = adminBody.RootElement.GetProperty("token").GetString()!;
            Check(jwt.ReadJwtToken(adminToken).Claims.Any(x => x.Type == "role" && x.Value == "Admin"),
                "Admin role claim is missing.");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
            using var adminWrite = await client.PostAsJsonAsync("/api/tags", new { tagName = "A2 Admin Tag" });
            Check(adminWrite.StatusCode == HttpStatusCode.Created, "Admin cannot write.");
            Console.WriteLine("PASS A2-06: public GET, 12 anonymous writes 401, invalid/expired 401, Staff CRUD and ownership, Admin write, CORS.");
        }
    }
}
finally
{
    if (!api.HasExited)
    {
        api.Kill(entireProcessTree: true);
        await api.WaitForExitAsync();
    }
}
