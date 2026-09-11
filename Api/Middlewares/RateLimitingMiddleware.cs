using System.Collections.Concurrent;

namespace Api.Middlewares;

/// <summary>
/// US-POS-Rate-Limit: Protects transaction-critical endpoints (order creation,
/// payment/confirm, refunds, stock adjustments) from duplicate submissions caused
/// by button spamming / double-clicks / retried requests.
///
/// It only acts on endpoints decorated with <see cref="RateLimitAttribute"/>.
/// Enforcement is a per-identity + per-endpoint sliding window held in memory.
/// On breach it short-circuits with HTTP 429 and a Retry-After header, so the
/// second (duplicate) click never reaches the controller and can't create a
/// second order/payment.
///
/// State is process-local (ConcurrentDictionary). For a single api-pos instance
/// this fully prevents duplicates. If api-pos is ever scaled horizontally, this
/// should be backed by a distributed store (e.g. Redis) — see notes in the class.
/// </summary>
public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private readonly bool _isEnabled;

    // key -> list of request timestamps (UTC ticks) within the tracked window.
    private static readonly ConcurrentDictionary<string, RequestWindow> _windows = new();

    // Housekeeping: purge stale windows so the dictionary doesn't grow unbounded.
    private static DateTime _lastSweep = DateTime.UtcNow;
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);
    private static readonly object _sweepLock = new();

    public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        // Toggle with RATE_LIMIT_ENABLED=false to disable (defaults to enabled).
        var flag = Environment.GetEnvironmentVariable("RATE_LIMIT_ENABLED");
        _isEnabled = string.IsNullOrWhiteSpace(flag)
            || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_isEnabled)
        {
            await _next(context);
            return;
        }

        // Only endpoints explicitly marked with [RateLimit] are throttled.
        var endpoint = context.GetEndpoint();
        var attribute = endpoint?.Metadata.GetMetadata<RateLimitAttribute>();
        if (attribute == null)
        {
            await _next(context);
            return;
        }

        var identity = ResolveIdentity(context);
        var routeKey = BuildRouteKey(context, endpoint!);
        var key = $"{identity}::{routeKey}";

        var window = _windows.GetOrAdd(key, _ => new RequestWindow());

        var now = DateTime.UtcNow;
        var windowSpan = TimeSpan.FromSeconds(Math.Max(1, attribute.WindowSeconds));

        bool allowed;
        int retryAfterSeconds = attribute.WindowSeconds;

        lock (window.SyncRoot)
        {
            // Drop timestamps that have aged out of the window.
            window.Timestamps.RemoveAll(ts => now - ts >= windowSpan);

            if (window.Timestamps.Count < Math.Max(1, attribute.MaxRequests))
            {
                window.Timestamps.Add(now);
                allowed = true;
            }
            else
            {
                allowed = false;
                // Retry-After = time until the oldest timestamp exits the window.
                var oldest = window.Timestamps[0];
                var wait = windowSpan - (now - oldest);
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            }
        }

        MaybeSweep(now);

        if (allowed)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "Rate limit hit: identity={Identity} route={Route} window={Window}s max={Max}",
            identity, routeKey, attribute.WindowSeconds, attribute.MaxRequests);

        var message = attribute.Message
            ?? "You're doing that too fast. Please wait a moment before trying again.";

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(new
        {
            message,
            retryAfterSeconds
        });
    }

    /// <summary>
    /// Prefer the authenticated user's stable GUID (survives IP/proxy changes).
    /// Fall back to client IP for unauthenticated/dev requests so anonymous
    /// callers are still de-duplicated.
    /// </summary>
    private static string ResolveIdentity(HttpContext context)
    {
        var user = context.GetCurrentUser();
        if (user != null && user.Id != Guid.Empty)
            return $"user:{user.Id}";

        if (!string.IsNullOrWhiteSpace(user?.Username))
            return $"user:{user!.Username}";

        var ip = context.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(ip) ? "anon" : $"ip:{ip}";
    }

    /// <summary>
    /// Key by HTTP method + matched route pattern (not the raw path), so that
    /// /refunds/1/approve and /refunds/2/approve share the same limiter bucket
    /// per user — a user shouldn't be hammering the approve action regardless of id.
    /// </summary>
    private static string BuildRouteKey(HttpContext context, Endpoint endpoint)
    {
        var routePattern = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        var route = string.IsNullOrWhiteSpace(routePattern)
            ? context.Request.Path.Value ?? string.Empty
            : routePattern;
        return $"{context.Request.Method}:{route}";
    }

    private static void MaybeSweep(DateTime now)
    {
        if (now - _lastSweep < _sweepInterval) return;

        // Only one thread sweeps; others skip.
        if (!Monitor.TryEnter(_sweepLock)) return;
        try
        {
            if (now - _lastSweep < _sweepInterval) return;
            _lastSweep = now;

            foreach (var kvp in _windows)
            {
                var w = kvp.Value;
                lock (w.SyncRoot)
                {
                    // A window is stale if its most recent hit is older than 10 minutes.
                    if (w.Timestamps.Count == 0 || now - w.Timestamps[^1] > TimeSpan.FromMinutes(10))
                    {
                        _windows.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }
        finally
        {
            Monitor.Exit(_sweepLock);
        }
    }

    private sealed class RequestWindow
    {
        public List<DateTime> Timestamps { get; } = new();
        public object SyncRoot { get; } = new();
    }
}
