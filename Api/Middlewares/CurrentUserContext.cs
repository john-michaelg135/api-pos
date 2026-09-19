using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Text.Json;

namespace Api.Middlewares;

/// <summary>
/// Canonical br-auth POS module names. Must match the auth service seeder and
/// the web-pos frontend (lib/permissions.ts POS_MODULES).
/// </summary>
public static class PosModules
{
    public const string Dashboard = "Dashboard";
    public const string SalesProcessing = "Sales Processing";
    public const string OrderManagement = "Order Management";
    public const string ProductManagement = "Product Management/Price Configuration";
    public const string SalesReports = "Sales Report Analytics";
    public const string Locations = "Location/Branch Management";
    public const string StockManagement = "Stock Management";
}

/// <summary>
/// Granular POS action flags for a single module, mirroring br-auth's
/// UserAppPermission (canRead/canWrite/canUpdate/canDelete/canApprove/canExport).
/// </summary>
public class ModulePermission
{
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
    public bool CanApprove { get; set; }
    public bool CanExport { get; set; }
}

public enum PosAction
{
    Read,
    Write,
    Update,
    Delete,
    Approve,
    Export
}

public class CurrentUserContext
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string[] Apps { get; set; } = Array.Empty<string>();
    public int? LocationId { get; set; }
    public string? SubRole { get; set; }

    /// <summary>br-auth role string (e.g. "Staff/Employee", "Super Admin").</summary>
    public string? Role { get; set; }

    /// <summary>True for br-auth Super Admin / CEO — bypasses all granular checks.</summary>
    public bool IsSuperUser { get; set; }

    /// <summary>Granular POS permissions per module (from the br-auth `permissions` claim).</summary>
    public Dictionary<string, ModulePermission> Permissions { get; set; } = new();

    /// <summary>
    /// Does the user have <paramref name="action"/> on the POS <paramref name="module"/>?
    /// Super users always pass.
    /// </summary>
    public bool Can(string module, PosAction action)
    {
        if (IsSuperUser) return true;
        if (!Permissions.TryGetValue(module, out var perm) || perm is null) return false;
        return action switch
        {
            PosAction.Read    => perm.CanRead,
            PosAction.Write   => perm.CanWrite,
            PosAction.Update  => perm.CanUpdate,
            PosAction.Delete  => perm.CanDelete,
            PosAction.Approve => perm.CanApprove,
            PosAction.Export  => perm.CanExport,
            _ => false
        };
    }
}

public static class HttpContextExtensions
{
    public static CurrentUserContext? GetCurrentUser(this HttpContext context)
    {
        if (context == null) return null;

        // 1. Try to read from Items first (populated by middleware / auth bridge)
        if (context.Items.TryGetValue("CurrentUser", out var userObj) && userObj is CurrentUserContext user)
        {
            return user;
        }

        // 2. If JwtBearer authenticated the request, build from the validated claims.
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var fromClaims = JwtClaims.FromPrincipal(context.User);
            if (fromClaims != null)
            {
                context.Items["CurrentUser"] = fromClaims;
                return fromClaims;
            }
        }

        // 3. Otherwise, parse from headers/cookies manually (works in dev or when bypass is active)
        var token = string.Empty;
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader["Bearer ".Length..].Trim();
        }
        else if (context.Request.Cookies.TryGetValue("sso_token", out var cookieToken))
        {
            token = cookieToken;
        }

        if (string.IsNullOrWhiteSpace(token)) return null;

        var contextUser = JwtClaims.ParseUser(token);
        if (contextUser == null) return null;

        context.Items["CurrentUser"] = contextUser;
        return contextUser;
    }
}

/// <summary>
/// Shared, dependency-free JWT payload parsing used by both the auth middleware
/// and the on-demand GetCurrentUser() extension so the two never drift.
/// </summary>
public static class JwtClaims
{
    public static CurrentUserContext? ParseUser(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;

            var payloadPart = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadPart.Length % 4)
            {
                case 2: payloadPart += "=="; break;
                case 3: payloadPart += "="; break;
            }

            var bytes = Convert.FromBase64String(payloadPart);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var idStr = root.TryGetProperty("sub", out var idProp) ? idProp.GetString() : null;
            var username = root.TryGetProperty("unique_name", out var userProp) ? userProp.GetString() : string.Empty;

            var apps = Array.Empty<string>();
            if (root.TryGetProperty("apps", out var appsProp))
            {
                var appsJson = appsProp.GetString();
                if (!string.IsNullOrEmpty(appsJson))
                {
                    apps = JsonSerializer.Deserialize<string[]>(appsJson) ?? Array.Empty<string>();
                }
            }

            int? locationId = null;
            if (root.TryGetProperty("location_id", out var locProp) && int.TryParse(locProp.GetString(), out var lid))
            {
                locationId = lid;
            }

            var subRole = root.TryGetProperty("sub_role", out var srProp) ? srProp.GetString() : null;

            // Role: OpenIddict may emit a single string or an array of roles.
            string? role = null;
            if (root.TryGetProperty("role", out var roleProp))
            {
                role = roleProp.ValueKind == JsonValueKind.Array
                    ? roleProp.EnumerateArray().Select(e => e.GetString()).FirstOrDefault(r => !string.IsNullOrEmpty(r))
                    : roleProp.GetString();
            }

            // isSuperUser: emitted as the string "true"/"false".
            var isSuperUser = false;
            if (root.TryGetProperty("isSuperUser", out var superProp))
            {
                isSuperUser = superProp.ValueKind == JsonValueKind.True
                    || string.Equals(superProp.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }

            var permissions = ParsePosPermissions(root);

            return new CurrentUserContext
            {
                Id = idStr != null && Guid.TryParse(idStr, out var gid) ? gid : Guid.Empty,
                Username = username ?? string.Empty,
                Apps = apps,
                LocationId = locationId,
                SubRole = subRole,
                Role = role,
                IsSuperUser = isSuperUser,
                Permissions = permissions
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the user context from a validated ClaimsPrincipal (populated by the
    /// JwtBearer handler). Preferred over ParseUser once authentication has run,
    /// since the token signature has already been verified against the issuer.
    /// </summary>
    public static CurrentUserContext? FromPrincipal(ClaimsPrincipal principal)
    {
        try
        {
            var idStr = principal.FindFirst("sub")?.Value
                        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var username = principal.FindFirst("unique_name")?.Value
                           ?? principal.FindFirst(ClaimTypes.Name)?.Value
                           ?? principal.Identity?.Name
                           ?? string.Empty;

            var apps = Array.Empty<string>();
            var appsJson = principal.FindFirst("apps")?.Value;
            if (!string.IsNullOrEmpty(appsJson))
            {
                try { apps = JsonSerializer.Deserialize<string[]>(appsJson) ?? Array.Empty<string>(); }
                catch { /* leave empty on malformed apps claim */ }
            }

            int? locationId = null;
            if (int.TryParse(principal.FindFirst("location_id")?.Value, out var lid))
            {
                locationId = lid;
            }

            var subRole = principal.FindFirst("sub_role")?.Value;

            // Roles may appear as multiple role claims; take the first non-empty.
            var role = principal.FindFirst(ClaimTypes.Role)?.Value
                       ?? principal.FindFirst("role")?.Value;

            var isSuperUser = string.Equals(
                principal.FindFirst("isSuperUser")?.Value, "true",
                StringComparison.OrdinalIgnoreCase);

            var permissions = ParsePosPermissionsFromString(principal.FindFirst("permissions")?.Value);

            return new CurrentUserContext
            {
                Id = idStr != null && Guid.TryParse(idStr, out var gid) ? gid : Guid.Empty,
                Username = username,
                Apps = apps,
                LocationId = locationId,
                SubRole = subRole,
                Role = role,
                IsSuperUser = isSuperUser,
                Permissions = permissions
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the br-auth `permissions` claim into the POS module map. The claim
    /// is a JSON string shaped like:
    ///   { "POS": { "Order Management": { "canRead": true, "canApprove": true, ... }, ... }, "CRMS": {...} }
    /// Only the POS app is retained. Super users receive "{}" and rely on IsSuperUser.
    /// </summary>
    private static Dictionary<string, ModulePermission> ParsePosPermissions(JsonElement root)
    {
        if (!root.TryGetProperty("permissions", out var permProp)) return new(StringComparer.Ordinal);

        // The raw-token claim may be a JSON string or (rarely) an inline object.
        var raw = permProp.ValueKind switch
        {
            JsonValueKind.String => permProp.GetString(),
            JsonValueKind.Object => permProp.GetRawText(),
            _ => null
        };
        return ParsePosPermissionsFromString(raw);
    }

    /// <summary>
    /// Parses the POS section of the br-auth `permissions` JSON string into the
    /// module map. Accepts the object form too. Returns an empty map for null,
    /// empty, "{}" (super users), or malformed input.
    /// </summary>
    private static Dictionary<string, ModulePermission> ParsePosPermissionsFromString(string? raw)
    {
        var result = new Dictionary<string, ModulePermission>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var permObj = doc.RootElement;
            if (permObj.ValueKind != JsonValueKind.Object) return result;
            if (!permObj.TryGetProperty("POS", out var posModules) || posModules.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var module in posModules.EnumerateObject())
            {
                if (module.Value.ValueKind != JsonValueKind.Object) continue;
                result[module.Name] = new ModulePermission
                {
                    CanRead    = GetFlag(module.Value, "canRead"),
                    CanWrite   = GetFlag(module.Value, "canWrite"),
                    CanUpdate  = GetFlag(module.Value, "canUpdate"),
                    CanDelete  = GetFlag(module.Value, "canDelete"),
                    CanApprove = GetFlag(module.Value, "canApprove"),
                    CanExport  = GetFlag(module.Value, "canExport"),
                };
            }
        }
        catch
        {
            // Malformed permissions claim — treat as no granular permissions.
        }

        return result;
    }

    private static bool GetFlag(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var flag)) return false;
        return flag.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(flag.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
