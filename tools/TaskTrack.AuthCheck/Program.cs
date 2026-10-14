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
if (stage is not ("register" or "login")) throw new ArgumentException("Unknown auth check stage.");
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
    if (stage == "login")
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
