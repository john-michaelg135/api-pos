namespace Api.Middlewares;

/// <summary>
/// Marks a controller action as rate-limited to prevent duplicate submissions
/// from button-spamming (e.g. double-clicking "Checkout" creating two orders).
///
/// The <see cref="RateLimitingMiddleware"/> enforces a per-user, per-endpoint
/// sliding window. Requests beyond the allowed count within the window receive
/// HTTP 429 (Too Many Requests) with a Retry-After header.
///
/// Usage:
///   [RateLimit(WindowSeconds = 5, MaxRequests = 1)]   // one submit per 5s
///   [RateLimit(WindowSeconds = 10, MaxRequests = 3)]  // burst of 3 per 10s
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RateLimitAttribute : Attribute
{
    /// <summary>Length of the sliding window, in seconds.</summary>
    public int WindowSeconds { get; set; } = 5;

    /// <summary>Maximum number of requests allowed within the window. Defaults to 1 (strict de-dupe).</summary>
    public int MaxRequests { get; set; } = 1;

    /// <summary>Optional human-friendly message returned in the 429 body.</summary>
    public string? Message { get; set; }
}
