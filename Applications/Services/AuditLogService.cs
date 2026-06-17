using Api.Contracts.Shared;
using Applications.Interfaces;
using Infrastructures.Externals;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Api.Middlewares;

namespace Applications.Services;

public class AuditLogService : IAuditLogService
{
    private readonly AuditLogClient _auditLogClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _isEnabled;

    public AuditLogService(AuditLogClient auditLogClient, IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _auditLogClient = auditLogClient;
        _httpContextAccessor = httpContextAccessor;
        _isEnabled = string.Equals(configuration["AUDIT_SERVICE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public void Log(string action, string entity, int entityId, object? before, object? after, Guid? performedBy)
    {
        if (!_isEnabled) return;

        // Try to get PerformedBy from context if not explicitly provided
        if (!performedBy.HasValue || performedBy.Value == Guid.Empty)
        {
            var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
            if (currentUser != null && currentUser.Id != Guid.Empty)
            {
                performedBy = currentUser.Id;
            }
        }

        var entry = new AuditLogEntry
        {
            Action = action,
            Entity = entity,
            EntityId = entityId,
            PerformedBy = performedBy,
            Timestamp = DateTime.UtcNow,
            Before = before,
            After = after
        };

        // Fire and forget
        _ = _auditLogClient.SendAsync(entry);
    }
}
