namespace Applications.Audit;

/// <summary>
/// Audit delivery settings. AUDIT_SERVICE_ENABLED is the rollout switch; POS_GATEWAY_URL is the
/// shared API gateway origin and POS_GATEWAY_CALLER_KEY the server-only key of its "pos" Caller.
/// The upstream audit-logs key stays in the gateway.
/// </summary>
public sealed class AuditDeliveryOptions
{
    public const string EnabledVariable = "AUDIT_SERVICE_ENABLED";
    public const string GatewayUrlVariable = "POS_GATEWAY_URL";
    public const string CallerKeyVariable = "POS_GATEWAY_CALLER_KEY";

    public bool Requested { get; init; }
    public string? GatewayUrl { get; init; }
    public string? CallerKey { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool IsComplete => !string.IsNullOrWhiteSpace(GatewayUrl) && !string.IsNullOrWhiteSpace(CallerKey)
        && Uri.TryCreate(GatewayUrl, UriKind.Absolute, out _);

    /// <summary>Disabled, or enabled with incomplete settings, means zero outbound calls.</summary>
    public bool Enabled => Requested && IsComplete;

    public static AuditDeliveryOptions FromEnvironment(Func<string, string?> read) => new()
    {
        Requested = string.Equals(read(EnabledVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
        GatewayUrl = read(GatewayUrlVariable)?.Trim().TrimEnd('/'),
        CallerKey = read(CallerKeyVariable)?.Trim(),
    };
}
