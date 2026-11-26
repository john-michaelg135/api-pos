using System.Net;
using System.Net.Http.Json;
using Applications.Interfaces;

namespace Applications.Services;

/// <summary>
/// Verifies a Portal account password with Neon Auth, the service the Enterprise Portal signs users in with.
/// A correct password makes Neon Auth open a session; it is signed out straight away, so the check
/// leaves no usable session behind. The password goes only to Neon Auth, over HTTPS, and is never logged.
/// </summary>
public sealed class NeonAuthPasswordVerifier : IAccountPasswordVerifier
{
    public const string HttpClientName = "NeonAuth";
    private const string SessionCookiePrefix = "__Secure-neon-auth";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NeonAuthPasswordVerifier> _logger;

    public NeonAuthPasswordVerifier(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NeonAuthPasswordVerifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PasswordCheckResult> VerifyAsync(string email, string password, CancellationToken cancellationToken)
    {
        var baseUrl = _configuration["NEON_AUTH_BASE_URL"]?.Trim().TrimEnd('/');
        // Neon Auth accepts sign-ins only from trusted origins; the Portal's origin is one of them.
        var origin = _configuration["PORTAL_URL"]?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(origin))
        {
            _logger.LogError("Account password verification is not configured (NEON_AUTH_BASE_URL or PORTAL_URL is missing).");
            return PasswordCheckResult.Unavailable;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var signIn = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sign-in/email")
            {
                Content = JsonContent.Create(new { email, password, rememberMe = false }),
            };
            signIn.Headers.TryAddWithoutValidation("Origin", origin);

            using var response = await client.SendAsync(signIn, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await SignOutAsync(client, baseUrl, origin, response, cancellationToken);
                return PasswordCheckResult.Verified;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return PasswordCheckResult.Incorrect;

            // 403 INVALID_ORIGIN means a misconfigured PORTAL_URL; 429 is Neon Auth's own throttle.
            _logger.LogWarning("Neon Auth password check returned HTTP {StatusCode}.", (int)response.StatusCode);
            return PasswordCheckResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Neon Auth password check failed with {ExceptionType}.", exception.GetType().Name);
            return PasswordCheckResult.Unavailable;
        }
    }

    private async Task SignOutAsync(
        HttpClient client,
        string baseUrl,
        string origin,
        HttpResponseMessage signInResponse,
        CancellationToken cancellationToken)
    {
        var cookies = signInResponse.Headers.TryGetValues("Set-Cookie", out var values)
            ? values
                .Select(value => value.Split(';', 2)[0].Trim())
                .Where(pair => pair.StartsWith(SessionCookiePrefix, StringComparison.Ordinal))
                .ToArray()
            : [];
        if (cookies.Length == 0)
        {
            _logger.LogWarning("Neon Auth verified a password but returned no session cookie to sign out.");
            return;
        }

        try
        {
            using var signOut = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sign-out")
            {
                Content = JsonContent.Create(new { }),
            };
            signOut.Headers.TryAddWithoutValidation("Origin", origin);
            signOut.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));

            // The check already succeeded, so a slow sign-out must not undo it for the manager.
            using var response = await client.SendAsync(signOut, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Neon Auth sign-out after a password check returned HTTP {StatusCode}.", (int)response.StatusCode);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Neon Auth sign-out after a password check failed with {ExceptionType}.", exception.GetType().Name);
        }
    }
}
