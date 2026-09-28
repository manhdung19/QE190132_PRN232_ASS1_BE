using Npgsql;

namespace TaskTrack.Repo.Configuration;

public static class PostgresConnection
{
    public static string FromUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("DATABASE_URL must be a PostgreSQL URL.");

        var credentials = uri.UserInfo.Split(':', 2);
        if (credentials.Length != 2 || string.IsNullOrEmpty(uri.Host)
            || uri.AbsolutePath.Trim('/').Length == 0)
            throw new InvalidOperationException("DATABASE_URL requires host, database, username and password.");

        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = Uri.UnescapeDataString(credentials[1]),
            SslMode = SslMode.Require,
            IncludeErrorDetail = false,
            Timeout = 15
        };

        foreach (var option in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = option.Split('=', 2);
            if (pair.Length != 2 || pair[0] != "sslmode")
                throw new InvalidOperationException("Unsupported DATABASE_URL query option.");
            connection.SslMode = pair[1] switch
            {
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException("DATABASE_URL requires TLS.")
            };
        }

        return connection.ConnectionString;
    }
}
