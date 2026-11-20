namespace Applications.StaffAlerts;

/// <summary>A Portal staff member POS may notify, with the Portal Modules granted now.</summary>
public sealed record PortalRecipient(string Id, string? Username, string? Name, string? Role, IReadOnlyList<string> Modules);

/// <summary>A Portal copy as the notification service holds it.</summary>
public sealed record RemoteCopy(Guid Id, bool Read);

public sealed record PortalCopyRequest(string IdempotencyKey, string Type, string Title, string Body, string? Link, string UserId);

/// <summary>
/// The Enterprise Portal recipient directory and the notification service behind the API gateway.
/// Every call is bounded; failures throw <see cref="StaffAlertGatewayException"/> with a category only.
/// </summary>
public interface IStaffAlertGateway
{
    Task<IReadOnlyList<PortalRecipient>> RecipientsAsync(CancellationToken cancellationToken);

    /// <summary>Create (or, on replay, recover) the copy for one recipient.</summary>
    Task<RemoteCopy> SendAsync(PortalCopyRequest copy, CancellationToken cancellationToken);

    /// <summary>Find an earlier copy by its idempotency key without creating one.</summary>
    Task<RemoteCopy?> FindAsync(string userId, string idempotencyKey, CancellationToken cancellationToken);

    Task<RemoteCopy?> StatusAsync(string userId, Guid id, CancellationToken cancellationToken);

    /// <summary>Mark one exact copy read. Repeating it is safe.</summary>
    Task MarkReadAsync(string userId, Guid id, CancellationToken cancellationToken);

    /// <summary>Content-free refresh notice on the POS staff channel.</summary>
    Task NudgeAsync(CancellationToken cancellationToken);

    /// <summary>A short-lived WebSocket address for the POS staff channel. Never log it.</summary>
    Task<string> LiveUrlAsync(CancellationToken cancellationToken);
}

public sealed class StaffAlertGatewayException(string category, bool transport = false) : Exception(category)
{
    public string Category { get; } = category;

    /// <summary>Network fault or timeout (as opposed to a refusal).</summary>
    public bool Transport { get; } = transport;
}
