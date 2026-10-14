using System.Text;
using Npgsql;
using TaskTrack.Repo.Configuration;
using TaskTrack.Service.Services;

static string ReadHiddenPassword()
{
    var chars = new StringBuilder();
    Console.Error.Write("Admin password: ");
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Length > 0) chars.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) chars.Append(key.KeyChar);
    }
    return chars.ToString();
}

if (args.Length != 2 && !(args.Length == 3 && args[2] == "--password-stdin"))
    throw new ArgumentException("Usage: dotnet run --project tools/TaskTrack.AdminSeed -- <full-name> <email> [--password-stdin]");

var fullName = args[0].Trim();
var email = args[1].Trim().ToLowerInvariant();
if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 150 ||
    !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) || email.Length > 320)
    throw new ArgumentException("Invalid name or email.");

var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") is { Length: > 0 } url
    ? PostgresConnection.FromUrl(url)
    : Environment.GetEnvironmentVariable("ConnectionStrings__TaskTrack")
      ?? throw new InvalidOperationException("Set DATABASE_URL or ConnectionStrings__TaskTrack.");

var password = args.Length == 3 ? await Console.In.ReadLineAsync() : ReadHiddenPassword();
if (password is null || password.Length is < 8 or > 128)
    throw new ArgumentException("Password must be 8–128 characters.");
var hash = PasswordHasher.Hash(password);
password = null;

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
await using var command = new NpgsqlCommand(
    "INSERT INTO public.\"SystemAccount\" (\"FullName\", \"Email\", \"PasswordHash\", \"Role\") " +
    "VALUES (@name, @email, @hash, 1) RETURNING \"AccountID\"", connection);
command.Parameters.AddWithValue("name", fullName);
command.Parameters.AddWithValue("email", email);
command.Parameters.AddWithValue("hash", hash);
try
{
    var id = (int)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Insert returned no ID."));
    Console.WriteLine($"Created Admin AccountID {id}.");
}
catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
{
    throw new InvalidOperationException("Email already exists.");
}
