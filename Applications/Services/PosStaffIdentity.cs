using System.Data;
using Api.Middlewares;
using Applications.Interfaces;
using Infrastructures.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Applications.Services;

public class PosStaffIdentity : IPosStaffIdentity
{
    private readonly PosDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private bool _resolved;
    private int? _staffId;

    public PosStaffIdentity(PosDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<int?> CurrentStaffIdAsync(CancellationToken cancellationToken = default)
    {
        if (_resolved) return _staffId;

        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        // Same key the manager PIN check uses.
        var authUserId = user?.PortalUserId ?? (user != null && user.Id != Guid.Empty ? user.Id.ToString() : null);
        if (user == null || string.IsNullOrWhiteSpace(authUserId))
        {
            _resolved = true;
            return null;
        }

        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            // Upsert so two first requests at once can't create two numbers for one person.
            command.CommandText = """
                INSERT INTO public."PosStaffUsers" ("AuthUserId", "Username", "DisplayName", "FirstSeenAt", "LastSeenAt")
                VALUES (@authUserId, @username, @displayName, now(), now())
                ON CONFLICT ("AuthUserId") DO UPDATE
                    SET "Username" = EXCLUDED."Username",
                        "DisplayName" = EXCLUDED."DisplayName",
                        "LastSeenAt" = EXCLUDED."LastSeenAt"
                RETURNING "Id";
                """;
            command.Parameters.Add(new NpgsqlParameter("authUserId", authUserId));
            command.Parameters.Add(new NpgsqlParameter("username", user.Username ?? string.Empty));
            command.Parameters.Add(new NpgsqlParameter("displayName", (object?)NullIfBlank(user.DisplayName) ?? DBNull.Value));
            _staffId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            _resolved = true;
            return _staffId;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    public async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> staffIds, CancellationToken cancellationToken = default)
    {
        var ids = staffIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<int, string>();
        var rows = await _db.PosStaffUsers
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Username })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName!);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
