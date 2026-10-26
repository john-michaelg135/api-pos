using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace Api.Middlewares;

public sealed class PortalSessionMiddleware
{
    private const string SessionCookie = "pos_session";
    private const string LocationsPath = "/api-pos/locations";
    private const string DashboardPath = "/api-pos/dashboard";
    private const string DashboardModule = "Dashboard";
    private const string LocationManagementModule = "Location Management";
    private const string SalesProcessingModule = "Sales Processing";
    private const string OrderManagementModule = "Order Management";
    private const string ProductManagementModule = "Product Management";
    private const string StockManagementModule = "Stock Management";
    private const string SalesReportsModule = "Sales Reports";
    private static readonly string[] PosModules =
    [
        SalesProcessingModule,
        OrderManagementModule,
        ProductManagementModule,
        StockManagementModule,
        SalesReportsModule,
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
        var requiredModule = GetRequiredModule(context.Request.Path, context.Request.Method);
        if (requiredModule is null)
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
        // Super Admin and CEO open the Dashboard whatever the Portal granted, so any POS Module proves who they are.
        var isDashboardRequest = context.Request.Path.StartsWithSegments(DashboardPath, StringComparison.OrdinalIgnoreCase);
        var isLocationManagementRequest =
            context.Request.Path.StartsWithSegments(LocationsPath, StringComparison.OrdinalIgnoreCase) &&
            !isMineRequest &&
            !(HttpMethods.IsGet(context.Request.Method) &&
              context.Request.Path.Equals(LocationsPath, StringComparison.OrdinalIgnoreCase));
        string[] modulesToValidate = isMineRequest
            ? PosModules
            : isDashboardRequest
            ? [DashboardModule, .. PosModules]
            : [requiredModule];
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
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && (isMineRequest || isDashboardRequest))
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

        if (isDashboardRequest && !AdministratorRoles.Contains(validation.Staff.Role))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Only the owner may view the Dashboard.");
            return;
        }

        if (isLocationManagementRequest && !AdministratorRoles.Contains(validation.Staff.Role))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Only Super Admins and CEOs can manage locations.");
            return;
        }

        if (!RoleMayUseModule(validation.Staff.Role, validatedModule))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Your POS role does not allow access to this module.");
            return;
        }

        context.Items["CurrentUser"] = new CurrentUserContext
        {
            PortalUserId = validation.UserId,
            Username = validation.Staff.Username!,
            DisplayName = validation.Staff.Name,
            Role = validation.Staff.Role,
            IsSuperUser = AdministratorRoles.Contains(validation.Staff.Role),
            Apps = ["POS"],
        };

        await _next(context);
    }

    private static bool IsMineRequest(PathString path) =>
        path.Value?.Equals($"{LocationsPath}/mine", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetRequiredModule(PathString path, string method)
    {
        if (path.StartsWithSegments(LocationsPath, StringComparison.OrdinalIgnoreCase))
        {
            if (IsMineRequest(path)) return LocationManagementModule;
            if (HttpMethods.IsGet(method) &&
                path.Equals(LocationsPath, StringComparison.OrdinalIgnoreCase))
            {
                return SalesProcessingModule;
            }

            return LocationManagementModule;
        }

        if (path.StartsWithSegments(DashboardPath, StringComparison.OrdinalIgnoreCase))
            return DashboardModule;
        if (path.StartsWithSegments("/api-pos/order-entry/orders/ecommerce", StringComparison.OrdinalIgnoreCase))
            return null;
        if (path.StartsWithSegments("/api-pos/order-entry", StringComparison.OrdinalIgnoreCase))
            return SalesProcessingModule;
        if (path.StartsWithSegments("/api-pos/order-management", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api-pos/refunds", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api-pos/starting-cash", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api-pos/user-pin", StringComparison.OrdinalIgnoreCase))
            return OrderManagementModule;
        if (path.StartsWithSegments("/api-pos/product-catalog", StringComparison.OrdinalIgnoreCase))
            return ProductManagementModule;
        if (path.StartsWithSegments("/api-pos/analytics", StringComparison.OrdinalIgnoreCase))
            return SalesReportsModule;
        if (path.StartsWithSegments("/api-pos/inventory", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api-pos/transfers", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api-pos/branches", StringComparison.OrdinalIgnoreCase))
            return StockManagementModule;

        return null;
    }

    private static bool RoleMayUseModule(string role, string module)
    {
        if (AdministratorRoles.Contains(role)) return true;

        return role switch
        {
            "Cashier" => module is SalesProcessingModule or OrderManagementModule,
            "POS Manager" or "Manager" => module is SalesProcessingModule
                or OrderManagementModule
                or ProductManagementModule
                or StockManagementModule
                or SalesReportsModule,
            _ => false,
        };
    }

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
