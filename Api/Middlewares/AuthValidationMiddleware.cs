namespace Api.Middlewares;

/// <summary>
/// US-POS-023: Validates the incoming Bearer JWT token on every secure request
/// by calling the shared api-auth validate-token endpoint.
/// 
/// Toggle with AUTH_MIDDLEWARE_ENABLED=true/false in environment variables.
/// When disabled, all requests pass through (useful during development).
/// </summary>
public class AuthValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthValidationMiddleware> _logger;
    private readonly bool _isEnabled;

    // Routes that bypass auth validation
    private static readonly string[] _publicRoutes =
    [
        "/openapi",
        "/swagger",
        "/health",
        "/hubs"
    ];

    public AuthValidationMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthValidationMiddleware> logger,
        IConfiguration configuration)
    {
        _next             = next;
        _httpClientFactory = httpClientFactory;
        _logger           = logger;

        // US-POS-023: Toggled via environment variable for safe deployment
        _isEnabled = string.Equals(
            configuration["AUTH_MIDDLEWARE_ENABLED"], "true",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip if middleware is disabled (e.g. during development)
        if (!_isEnabled)
        {
            await _next(context);
            return;
        }

        // Skip public/dev routes
        var path = context.Request.Path.Value ?? string.Empty;
        if (_publicRoutes.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Extract Bearer token
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("US-POS-023: Request to {Path} rejected — missing or malformed Authorization header.", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Unauthorized: Missing or invalid Authorization header." });
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();

        // Forward token to api-auth validate-token endpoint
        try
        {
            var client = _httpClientFactory.CreateClient("AuthService");
            var request = new HttpRequestMessage(HttpMethod.Post, "/api-auth/validate-token");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("US-POS-023: Token validation failed for {Path} — auth service returned {Status}.", path, response.StatusCode);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "Unauthorized: Token validation failed." });
                return;
            }

            // Validation succeeded. Decode the JWT and populate CurrentUserContext.
            // This prevents services from needing to decode it manually on every call.
            PopulateCurrentUserContext(context, token);
        }
        catch (HttpRequestException ex)
        {
            // Auth service is unreachable — fail closed (block the request)
            _logger.LogError(ex, "US-POS-023: Auth service unreachable. Blocking request to {Path}.", path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { message = "Auth service is unavailable. Please try again later." });
            return;
        }

        await _next(context);
    }

    private void PopulateCurrentUserContext(HttpContext context, string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return;
            
            var payloadPart = parts[1];
            payloadPart = payloadPart.Replace('-', '+').Replace('_', '/');
            switch (payloadPart.Length % 4)
            {
                case 2: payloadPart += "=="; break;
                case 3: payloadPart += "="; break;
            }
            
            var bytes = Convert.FromBase64String(payloadPart);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var idStr = root.TryGetProperty("sub", out var idProp) ? idProp.GetString() : null;
            var username = root.TryGetProperty("unique_name", out var userProp) ? userProp.GetString() : string.Empty;
            
            var apps = Array.Empty<string>();
            if (root.TryGetProperty("apps", out var appsProp))
            {
                var appsJson = appsProp.GetString();
                if (!string.IsNullOrEmpty(appsJson))
                {
                    apps = System.Text.Json.JsonSerializer.Deserialize<string[]>(appsJson) ?? Array.Empty<string>();
                }
            }

            int? locationId = null;
            if (root.TryGetProperty("location_id", out var locProp))
            {
                if (int.TryParse(locProp.GetString(), out var lid))
                {
                    locationId = lid;
                }
            }

            var subRole = root.TryGetProperty("sub_role", out var roleProp) ? roleProp.GetString() : null;

            var contextUser = new CurrentUserContext
            {
                Id = idStr != null && Guid.TryParse(idStr, out var gid) ? gid : Guid.Empty,
                Username = username ?? string.Empty,
                Apps = apps,
                LocationId = locationId,
                SubRole = subRole
            };

            context.Items["CurrentUser"] = contextUser;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JWT payload to populate CurrentUserContext.");
        }
    }
}
