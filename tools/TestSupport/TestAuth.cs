using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TaskTrack.Repo.Configuration;

internal static class TestAuth
{
    private const string LocalSecret = "LocalRegressionCheckSecretOnly_12345678901234567890";

    public static string ResolveConnectionString(string? databaseUrl)
    {
        var local = Environment.GetEnvironmentVariable("A2_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(local))
        {
            var connection = new NpgsqlConnectionStringBuilder(local);
            if (connection.Host is not ("127.0.0.1" or "localhost") ||
                connection.Database != "tasktrack_a2_test")
                throw new InvalidOperationException("Regression connection must use local tasktrack_a2_test.");
            return connection.ConnectionString;
        }
        return PostgresConnection.FromUrl(databaseUrl ?? throw new InvalidOperationException("Supply DATABASE_URL."));
    }

    public static void ConfigureHost(ProcessStartInfo start) => start.Environment["JWT_SECRET"] = LocalSecret;

    public static async Task<string> LoginStaffAsync(HttpClient client, string scope)
    {
        var email = $"qa-{scope}-{Guid.NewGuid():N}@example.invalid";
        const string password = "ExamplePass123!";
        using var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Regression Staff", email, password
        });
        if (register.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"Regression Staff registration failed: {register.StatusCode}");
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        if (login.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Regression Staff login failed: {login.StatusCode}");
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Login response has no token.");
    }

    public static void Authorize(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public static int AccountId(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var body = JsonDocument.Parse(Convert.FromBase64String(payload));
        var id = body.RootElement.GetProperty("accountId");
        return id.ValueKind == JsonValueKind.String ? int.Parse(id.GetString()!) : id.GetInt32();
    }
}
