using Npgsql;

namespace Infrastructures.Persistence;

/// <summary>
/// Builds the PostgreSQL connection string from environment variables, supporting
/// both local development (split POSTGRES_* vars) and managed cloud Postgres
/// (a single DATABASE_URL, e.g. Neon / Render / Supabase).
///
/// Resolution order:
///   1. DATABASE_URL — accepts either the "postgresql://user:pass@host:port/db"
///      URI form (what Neon/Render hand you) or a raw Npgsql "Key=Value;" string.
///   2. Split vars — POSTGRES_DB_HOST / POSTGRES_DB_PORT / POSTGRES_USERNAME /
///      POSTGRES_PASSWORD (+ optional POSTGRES_DB, defaults to "pos_db").
///
/// SSL is required automatically for remote hosts (anything that is not
/// localhost / a docker service name), which managed providers mandate.
/// </summary>
public static class ConnectionStringFactory
{
    private const string DefaultDatabase = "pos_db";

    public static string Build()
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return NormalizeDatabaseUrl(databaseUrl.Trim());
        }

        return BuildFromSplitVars();
    }

    private static string NormalizeDatabaseUrl(string databaseUrl)
    {
        // Already an Npgsql key=value string (no URI scheme) — enrich and return.
        if (!databaseUrl.Contains("://", StringComparison.Ordinal))
        {
            var raw = new NpgsqlConnectionStringBuilder(databaseUrl);
            ApplySsl(raw);
            return raw.ConnectionString;
        }

        // URI form: postgresql://user:password@host:port/database?sslmode=require
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? string.Empty),
            Password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? string.Empty),
            Database = uri.AbsolutePath.Trim('/') is { Length: > 0 } db ? db : DefaultDatabase,
        };

        // Translate libpq-style query params (Neon appends these) into their
        // Npgsql connection-string equivalents.
        ApplyQueryParameters(builder, uri.Query);

        ApplySsl(builder);
        return builder.ConnectionString;
    }

    private static string BuildFromSplitVars()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_DB_HOST"),
            Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_DB_PORT"), out var port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") is { Length: > 0 } db ? db : DefaultDatabase,
            Username = Environment.GetEnvironmentVariable("POSTGRES_USERNAME"),
            Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"),
        };

        ApplySsl(builder);
        return builder.ConnectionString;
    }

    /// <summary>
    /// Maps libpq-style URI query parameters (e.g. Neon's
    /// "?sslmode=require&amp;channel_binding=require") onto the Npgsql builder.
    /// Unknown parameters are ignored so the connection string stays valid.
    /// </summary>
    private static void ApplyQueryParameters(NpgsqlConnectionStringBuilder builder, string query)
    {
        query = query.TrimStart('?');
        if (string.IsNullOrEmpty(query)) return;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]).Trim().ToLowerInvariant();
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]).Trim() : string.Empty;

            switch (key)
            {
                case "sslmode":
                    if (Enum.TryParse<SslMode>(value, ignoreCase: true, out var mode))
                    {
                        builder.SslMode = mode;
                    }
                    break;
                case "channel_binding":
                    // libpq: "require" | "prefer" | "disable". Npgsql: Require/Prefer/Disable.
                    if (Enum.TryParse<ChannelBinding>(value, ignoreCase: true, out var cb))
                    {
                        builder.ChannelBinding = cb;
                    }
                    break;
                // Other libpq params (application_name, options, etc.) are ignored.
            }
        }
    }

    /// <summary>
    /// Managed Postgres providers require TLS. Enable SSL for any non-local host
    /// unless the caller has already opted into a stricter mode (VerifyCA /
    /// VerifyFull). Local hosts are left as-is so docker/localhost connections
    /// don't fail on a missing certificate.
    /// </summary>
    private static void ApplySsl(NpgsqlConnectionStringBuilder builder)
    {
        if (IsLocalHost(builder.Host)) return;

        // Don't downgrade a stricter, explicitly-chosen mode.
        if (builder.SslMode == SslMode.VerifyCA || builder.SslMode == SslMode.VerifyFull)
        {
            return;
        }

        builder.SslMode = SslMode.Require;
    }

    private static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        return host is "localhost" or "127.0.0.1" or "::1" or "db" or "postgres"
            || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }
}
