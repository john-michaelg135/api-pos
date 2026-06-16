namespace Applications.Interfaces;

public interface IAuditLogService
{
    void Log(string action, string entity, int entityId, object? before, object? after, Guid? performedBy);
}
