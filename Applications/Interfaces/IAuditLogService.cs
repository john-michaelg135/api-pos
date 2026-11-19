using Api.Contracts.Shared;
using Applications.Audit;

namespace Applications.Interfaces;

/// <summary>
/// The one POS audit boundary. Call it once, after the change it describes has committed and outside
/// its transaction. It never throws: delivery is bounded and best effort, so an outage leaves gaps
/// rather than failing the sale or refund.
/// </summary>
public interface IAuditLogService
{
    Task RecordAsync(AuditEvent auditEvent);
}

/// <summary>Delivers one finished body. The gateway adapter is <c>AuditLogClient</c>; tests inject fakes.</summary>
public interface IAuditWriter
{
    Task<AuditWriteResult> WriteAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);
}
