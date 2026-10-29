using System.Security.Cryptography;
using System.Text;

namespace Api.Middlewares;

/// <summary>
/// Service-to-service sign-in for the Supply Chain system (SCMS). SCMS is a server, not a
/// signed-in staff member, so it sends <c>Authorization: Bearer &lt;SCMS_SERVICE_KEY&gt;</c>
/// instead of a POS session cookie. The key is accepted only on the routes SCMS is meant
/// to call (see <see cref="IsServiceRoute"/>); every other route still needs a staff session.
/// With no key configured the feature is off and nothing is opened up.
/// </summary>
public static class ScmsServiceAuth
{
    public const string KeySetting = "SCMS_SERVICE_KEY";

    // A short shared secret is guessable; refuse to use one.
    private const int MinimumKeyLength = 32;

    public enum Outcome
    {
        /// <summary>Not a service call (no bearer key, or not an SCMS route): use the staff session.</summary>
        NotAttempted,
        /// <summary>Valid key on an SCMS route: the request runs as the SCMS service.</summary>
        Authenticated,
        /// <summary>A bearer key was sent to an SCMS route but it is wrong (or the feature is off).</summary>
        Rejected,
    }

    /// <summary>The routes SCMS calls, per SCMS-POS-INTEGRATION-SPEC.md.</summary>
    public static bool IsServiceRoute(PathString path, string method)
    {
        // Module 2: SCMS posts a transfer manifest.
        if (HttpMethods.IsPost(method) && path.Equals("/api-pos/transfers", StringComparison.OrdinalIgnoreCase))
            return true;

        // Module 4: SCMS reads the branch list and a branch's live stock.
        if (HttpMethods.IsGet(method) && path.StartsWithSegments("/api-pos/branches", StringComparison.OrdinalIgnoreCase))
            return true;

        // Module 1: SCMS pushes product and variation updates.
        if (HttpMethods.IsPost(method) && path.Equals("/api-pos/product-catalog/sync-product", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static Outcome TryAuthenticate(HttpContext context, IConfiguration configuration, ILogger logger)
    {
        if (!IsServiceRoute(context.Request.Path, context.Request.Method))
            return Outcome.NotAttempted;

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Outcome.NotAttempted;

        var sent = header["Bearer ".Length..].Trim();
        var expected = configuration[KeySetting];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < MinimumKeyLength)
        {
            logger.LogWarning("SCMS called {Path} with a service key, but {Setting} is not set (or is shorter than {Min} characters).",
                context.Request.Path, KeySetting, MinimumKeyLength);
            return Outcome.Rejected;
        }

        // Constant-time comparison so the key can't be worked out from response timing.
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(sent),
            Encoding.UTF8.GetBytes(expected));
        if (!matches)
        {
            logger.LogWarning("Rejected an SCMS service call to {Path}: wrong service key.", context.Request.Path);
            return Outcome.Rejected;
        }

        // Runs as an administrator-level service identity so it can read any branch's stock.
        context.Items["CurrentUser"] = new CurrentUserContext
        {
            Username = "scms-service",
            DisplayName = "Supply Chain System",
            Role = "SCMS Service",
            IsSuperUser = true,
            Apps = ["SCMS"],
        };
        return Outcome.Authenticated;
    }
}
