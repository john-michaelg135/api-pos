namespace Applications.Services;

/// <summary>
/// How a refund is paid out. Company rule: the money goes back the way it came in. Paid in
/// cash means refunded in cash from the drawer; paid by GCash means refunded to the customer's
/// GCash. Old COD orders were settled in cash, so they count as cash.
/// </summary>
public static class RefundMethods
{
    public const string Cash = "Cash";
    public const string GCash = "GCash";

    public static string For(string? paymentMethod) =>
        paymentMethod?.Trim().ToLowerInvariant() is "cash" or "cod" or "cash on delivery" ? Cash : GCash;
}
