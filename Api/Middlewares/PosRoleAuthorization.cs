using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Api.Middlewares;

public static class PosRoleAuthorization
{
    private static readonly HashSet<string> AdministratorRoles = new(StringComparer.Ordinal)
    {
        "Super Admin",
        "CEO",
    };

    public static bool IsAdministrator(CurrentUserContext? user) =>
        user?.IsSuperUser == true ||
        (user?.Role is { } role && AdministratorRoles.Contains(role));

    public static bool IsManager(CurrentUserContext? user) =>
        IsAdministrator(user) || user?.Role is "POS Manager" or "Manager";

    public static bool IsCashierOrManager(CurrentUserContext? user) =>
        IsManager(user) || user?.Role == "Cashier";

    public static IActionResult? RequireManager(HttpContext context)
    {
        var user = context.GetCurrentUser();
        if (user is null)
            return new UnauthorizedObjectResult(new { message = "A valid POS session is required." });

        if (!IsManager(user))
            return new ObjectResult(new { message = "Only POS Managers and administrators may perform this action." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };

        return null;
    }

    public static IActionResult? RequireAdministrator(HttpContext context)
    {
        var user = context.GetCurrentUser();
        if (user is null)
            return new UnauthorizedObjectResult(new { message = "A valid POS session is required." });

        if (!IsAdministrator(user))
            return new ObjectResult(new { message = "Only the owner may view this." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };

        return null;
    }

    public static IActionResult? RequireCashierOrManager(HttpContext context)
    {
        var user = context.GetCurrentUser();
        if (user is null)
            return new UnauthorizedObjectResult(new { message = "A valid POS session is required." });

        if (!IsCashierOrManager(user))
            return new ObjectResult(new { message = "Your POS role may not perform this action." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };

        return null;
    }

    public static IActionResult? RequireLocationAccess(HttpContext context, int? locationId)
    {
        var user = context.GetCurrentUser();
        if (user is null)
            return new UnauthorizedObjectResult(new { message = "A valid POS session is required." });

        if (IsAdministrator(user)) return null;

        if (!locationId.HasValue || !user.LocationIds.Contains(locationId.Value))
        {
            return new ObjectResult(new { message = "You do not have access to this location." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }

        return null;
    }
}
