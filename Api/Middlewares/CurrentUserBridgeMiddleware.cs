namespace Api.Middlewares;

/// <summary>
/// Runs after JwtBearer authentication. It enforces that non-public requests are
/// authenticated (returning 401 otherwise) and projects the validated token
/// claims into <see cref="CurrentUserContext"/> stored in HttpContext.Items, so
/// the many services that call <c>HttpContext.GetCurrentUser()</c> continue to
/// work without change.
///
/// This replaces the previous AuthValidationMiddleware that called the Auth
/// Service's /api-auth/validate-token endpoint on every request. Token validation
/// now happens locally in the JwtBearer handler against the issuer's OIDC keys.
/// </summary>
public class CurrentUserBridgeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CurrentUserBridgeMiddleware> _logger;

    // Routes that never require authentication.
    private static readonly string[] _publicRoutes =
    [
        "/openapi",
        "/swagger",
        "/health",
        "/hubs"
    ];

    public CurrentUserBridgeMiddleware(RequestDelegate next, ILogger<CurrentUserBridgeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (_publicRoutes.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning("US-POS-023: Request to {Path} rejected — no valid bearer token.", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Unauthorized: a valid bearer token is required." });
            return;
        }

        // Project validated claims into CurrentUserContext for downstream services.
        var user = JwtClaims.FromPrincipal(context.User);
        if (user != null)
        {
            context.Items["CurrentUser"] = user;
        }

        await _next(context);
    }
}
