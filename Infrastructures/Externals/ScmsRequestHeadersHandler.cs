using System.Net.Http.Headers;

namespace Infrastructures.Externals;

/// <summary>
/// Adds the headers SCMS-POS-INTEGRATION-SPEC.md asks for (section 2.2) to every call POS
/// makes to SCMS: the service key SCMS gave us (SCMS_API_KEY) as a Bearer token, a fresh
/// X-Correlation-ID per request, and X-Source-System: POS. With no key set, calls still go
/// out without it (and SCMS will refuse them); a warning is logged once.
/// </summary>
public sealed class ScmsRequestHeadersHandler : DelegatingHandler
{
    public const string KeySetting = "SCMS_API_KEY";

    private static int _warnedMissingKey;

    private readonly IConfiguration _configuration;
    private readonly ILogger<ScmsRequestHeadersHandler> _logger;

    public ScmsRequestHeadersHandler(IConfiguration configuration, ILogger<ScmsRequestHeadersHandler> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = _configuration[KeySetting];
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        }
        else if (Interlocked.Exchange(ref _warnedMissingKey, 1) == 0)
        {
            _logger.LogWarning("{Setting} is not set, so calls to SCMS go out without a service key.", KeySetting);
        }

        request.Headers.Remove("X-Correlation-ID");
        request.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString());
        request.Headers.Remove("X-Source-System");
        request.Headers.Add("X-Source-System", "POS");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return base.SendAsync(request, cancellationToken);
    }
}
