namespace Applications.StaffAlerts;

/// <summary>
/// Staff bell settings. Alerts are always recorded and served to the POS bell. Remote work — Portal
/// copies, read synchronization and the refresh notice — runs only while STAFF_ALERTS_ENABLED=true
/// and the gateway (POS_GATEWAY_URL / POS_GATEWAY_CALLER_KEY, shared with audit and file storage)
/// and Portal directory (PORTAL_URL / PORTAL_SYSTEM_NAME / PORTAL_SYSTEM_SECRET) are configured.
/// Turning it off pauses sends; pending copies stay saved and go out when it is turned back on.
/// </summary>
public sealed class StaffAlertOptions
{
    public const string EnabledVariable = "STAFF_ALERTS_ENABLED";
    public const string GatewayUrlVariable = "POS_GATEWAY_URL";
    public const string CallerKeyVariable = "POS_GATEWAY_CALLER_KEY";
    public const string SendingAppVariable = "POS_NOTIFICATIONS_SENDING_APP";
    public const string StaffChannelVariable = "POS_NOTIFICATIONS_STAFF_CHANNEL";
    public const string WebOriginVariable = "POS_WEB_ORIGIN";

    public bool Requested { get; init; }
    public string? GatewayUrl { get; init; }
    public string? CallerKey { get; init; }
    public string? PortalUrl { get; init; }
    public string PortalSystemName { get; init; } = "POS";
    public string? PortalSystemSecret { get; init; }

    /// <summary>The gateway's registered sender name for POS, in its exact case.</summary>
    public string SendingApp { get; init; } = "pos";

    /// <summary>Shared live channel identity every POS bell listens on.</summary>
    public string StaffChannel { get; init; } = "pos-staff";

    /// <summary>Public POS web origin for absolute links in Portal copies (no trailing slash).</summary>
    public string? WebOrigin { get; init; }

    /// <summary>Deadline for one gateway or Portal call.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool HasGateway => !string.IsNullOrWhiteSpace(CallerKey) && IsHttpUrl(GatewayUrl);
    public bool HasDirectory => !string.IsNullOrWhiteSpace(PortalSystemSecret) && IsHttpUrl(PortalUrl);

    /// <summary>Portal copies, read sync and refresh notices go out.</summary>
    public bool SendsEnabled => Requested && HasGateway && HasDirectory;

    /// <summary>The bell can get a live ticket (otherwise it polls).</summary>
    public bool LiveEnabled => HasGateway;

    public string? AbsoluteLink(string path) =>
        string.IsNullOrWhiteSpace(WebOrigin) ? null : $"{WebOrigin}{path}";

    public static StaffAlertOptions FromEnvironment(Func<string, string?> read)
    {
        var webOrigin = read(WebOriginVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(webOrigin))
            webOrigin = read("WEB_POS_URL")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        webOrigin = IsHttpUrl(webOrigin) ? webOrigin!.TrimEnd('/') : null;

        return new StaffAlertOptions
        {
            Requested = string.Equals(read(EnabledVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            GatewayUrl = read(GatewayUrlVariable)?.Trim().TrimEnd('/'),
            CallerKey = read(CallerKeyVariable)?.Trim(),
            PortalUrl = read("PORTAL_URL")?.Trim().TrimEnd('/'),
            PortalSystemName = string.IsNullOrWhiteSpace(read("PORTAL_SYSTEM_NAME")) ? "POS" : read("PORTAL_SYSTEM_NAME")!.Trim(),
            PortalSystemSecret = read("PORTAL_SYSTEM_SECRET")?.Trim(),
            SendingApp = string.IsNullOrWhiteSpace(read(SendingAppVariable)) ? "pos" : read(SendingAppVariable)!.Trim(),
            StaffChannel = string.IsNullOrWhiteSpace(read(StaffChannelVariable)) ? "pos-staff" : read(StaffChannelVariable)!.Trim(),
            WebOrigin = webOrigin,
        };
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
