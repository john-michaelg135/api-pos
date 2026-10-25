using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace Api.Middlewares;

public sealed class PortalSessionMiddleware
{
    private const string SessionCookie = "pos_session";
    private const string LocationsPath = "/api-pos/locations";
    private const string LocationManagementModule = "Location Management";
    private static readonly string[] PosModules =
    [
        LocationManagementModule,
        "Dashboard",
        "Sales Processing",
        "Order Management",
        "Product Management",
        "Stock Management",
        "Sales Reports",
    ];
    private static readonly HashSet<string> AdministratorRoles = new(StringComparer.Ordinal)
    {
        "Super Admin",
        "CEO",
    };

    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PortalSessionMiddleware> _logger;

    public PortalSessionMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PortalSessionMiddleware> logger)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(LocationsPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Cookies.TryGetValue(SessionCookie, out var handle) ||
            string.IsNullOrWhiteSpace(handle))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "A valid POS session is required.");
            return;
        }

        var portalUrl = _configuration["PORTAL_URL"]?.TrimEnd('/');
        var systemName = _configuration["PORTAL_SYSTEM_NAME"] ?? "POS";
        var secret = _configuration["PORTAL_SYSTEM_SECRET"];
        if (string.IsNullOrWhiteSpace(portalUrl) || string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError("Enterprise Portal authentication is not configured for api-pos.");
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "Enterprise Portal authentication is not configured.");
            return;
        }

        var isMineRequest = IsMineRequest(context.Request.Path);
        string[] modulesToValidate = isMineRequest
            ? PosModules
            : [LocationManagementModule];
        PortalValidationResponse? validation = null;
        var validatedModule = string.Empty;
        try
        {
            var client = _httpClientFactory.CreateClient("EnterprisePortal");
            foreach (var module in modulesToValidate)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{portalUrl}/api/integrations/validate")
                {
                    Content = JsonContent.Create(new { handle, module }),
                };
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{systemName}:{secret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

                using var response = await client.SendAsync(request, context.RequestAborted);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && isMineRequest)
                {
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var error = await response.Content.ReadFromJsonAsync<PortalError>(
                        cancellationToken: context.RequestAborted);
                    if (string.Equals(error?.Code, "invalid_handle", StringComparison.Ordinal))
                    {
                        await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Your POS session is invalid or expired.");
                        return;
                    }

                    _logger.LogError("Enterprise Portal rejected the configured POS client credentials.");
                    await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "POS integration authentication is misconfigured.");
                    return;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "You do not have access to Location Management.");
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Enterprise Portal validation returned HTTP {StatusCode}.", (int)response.StatusCode);
                    await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "Enterprise Portal validation is temporarily unavailable.");
                    return;
                }

                validation = await response.Content.ReadFromJsonAsync<PortalValidationResponse>(
                    cancellationToken: context.RequestAborted);
                validatedModule = module;
                break;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Enterprise Portal validation failed with {ExceptionType}.", exception.GetType().Name);
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "Enterprise Portal validation is temporarily unavailable.");
            return;
        }

        if (validation is null)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "You do not have access to a POS module.");
            return;
        }

        if (validation is not { Active: true } ||
            !string.Equals(validation.System, systemName, StringComparison.Ordinal) ||
            !string.Equals(validation.Module, validatedModule, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(validation.UserId) ||
            validation.Staff is null ||
            string.IsNullOrWhiteSpace(validation.Staff.Username) ||
            string.IsNullOrWhiteSpace(validation.Staff.Role))
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "Enterprise Portal returned an invalid validation response.");
            return;
        }

        if (!isMineRequest && !AdministratorRoles.Contains(validation.Staff.Role))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Only Super Admins and CEOs can manage locations.");
            return;
        }

        context.Items["CurrentUser"] = new CurrentUserContext
        {
            PortalUserId = validation.UserId,
            Username = validation.Staff.Username!,
            Role = validation.Staff.Role,
            IsSuperUser = AdministratorRoles.Contains(validation.Staff.Role),
            Apps = ["POS"],
        };

        await _next(context);
    }

    private static bool IsMineRequest(PathString path) =>
        path.Value?.Equals($"{LocationsPath}/mine", StringComparison.OrdinalIgnoreCase) == true;

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted);
    }

    private sealed record PortalValidationResponse(
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("system")] string? System,
        [property: JsonPropertyName("module")] string? Module,
        [property: JsonPropertyName("staff")] PortalStaff? Staff);

    private sealed record PortalStaff(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("role")] string? Role);

    private sealed record PortalError(
        [property: JsonPropertyName("error")] string? Code);
}
