namespace Applications.StaffAlerts;

/// <summary>
/// Wakes the delivery worker after a commit that created or read alerts, so Portal copies and the
/// refresh notice go out promptly instead of at the next poll. Losing a wake only costs latency.
/// </summary>
public static class StaffAlertSignal
{
    private static readonly SemaphoreSlim Pending = new(0, 1);
    private static int _nudgeRequested;

    public static void Wake()
    {
        try { if (Pending.CurrentCount == 0) Pending.Release(); }
        catch (SemaphoreFullException) { /* already awake */ }
    }

    /// <summary>Ask for a refresh notice even without new alerts (e.g. shared read state changed).</summary>
    public static void RequestRefresh()
    {
        Interlocked.Exchange(ref _nudgeRequested, 1);
        Wake();
    }

    public static bool TakeRefreshRequest() => Interlocked.Exchange(ref _nudgeRequested, 0) == 1;

    public static Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Pending.WaitAsync(timeout, cancellationToken);
}
