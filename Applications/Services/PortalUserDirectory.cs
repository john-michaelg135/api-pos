using Applications.Interfaces;
using Infrastructures.Persistence;
using Npgsql;

namespace Applications.Services;

public sealed class PortalUserDirectory : IPortalUserDirectory
{
    private const string EligibleUsersSql = """
        SELECT u.id, u.username, u."firstName", u."lastName", u.email, r.name AS role
        FROM public."User" u
        JOIN public."Role" r ON r.id = u."roleId"
        WHERE u."isDeleted" = false
          AND u."mustChangePassword" = false
          AND r.name NOT IN ('Super Admin', 'CEO')
          AND EXISTS (
              SELECT 1
              FROM public."AccessGrant" ag
              JOIN public."Module" m ON m.id = ag."moduleId"
              JOIN public."System" s ON s.id = m."systemId"
              WHERE ag."userId" = u.id
                AND ag."revokedAt" IS NULL
                AND s.name = 'POS'
          )
        """;

    private readonly IConfiguration _configuration;

    public PortalUserDirectory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<PortalPosUser>> ListEligiblePosUsersAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTransactionReadOnlyAsync(connection, transaction, cancellationToken);

        await using var command = new NpgsqlCommand(
            $"{EligibleUsersSql} ORDER BY u.\"firstName\", u.\"lastName\", u.username",
            connection,
            transaction);
        var users = new List<PortalPosUser>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(ReadUser(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return users;
    }

    public async Task<PortalPosUser?> FindEligiblePosUserAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTransactionReadOnlyAsync(connection, transaction, cancellationToken);

        await using var command = new NpgsqlCommand(
            $"{EligibleUsersSql} AND u.id = @userId LIMIT 1",
            connection,
            transaction);
        command.Parameters.AddWithValue("userId", userId);

        PortalPosUser? user;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            user = await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
        }

        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    private string GetConnectionString()
    {
        var databaseUrl = _configuration["PORTAL_DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            throw new PortalDirectoryNotConfiguredException();
        }

        return ConnectionStringFactory.Normalize(databaseUrl);
    }

    private static async Task SetTransactionReadOnlyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PortalPosUser ReadUser(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
}

public sealed class PortalDirectoryNotConfiguredException : Exception
{
}
