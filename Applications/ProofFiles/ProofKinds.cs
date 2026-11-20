namespace Applications.ProofFiles;

/// <summary>The two order workflows that keep a proof photo.</summary>
public static class ProofKinds
{
    public const string Delivery = "delivery";
    public const string Refund = "refund";

    public static bool IsKnown(string? kind) => kind is Delivery or Refund;

    /// <summary>Link role stored on the File, e.g. "refund-proof".</summary>
    public static string LinkRole(string kind) => kind switch
    {
        Delivery => "delivery-proof",
        Refund => "refund-proof",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown proof kind."),
    };

    public static FileLink OrderLink(int orderId, string kind) =>
        new("POS", "order", orderId.ToString(System.Globalization.CultureInfo.InvariantCulture), LinkRole(kind));

    public static string DefaultFilename(string kind) => $"{kind}-proof.jpg";
}

/// <summary>A file-storage Link: discovery only; POS still authorizes every read.</summary>
public sealed record FileLink(string System, string EntityType, string EntityId, string Role);
