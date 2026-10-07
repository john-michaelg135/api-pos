namespace Domains.Entities;

public class PosUserPin
{
    public int Id { get; set; }
    
    /// <summary>br-auth user id (OIDC "sub" GUID).</summary>
    public string AuthUserId { get; set; } = string.Empty;

    /// <summary>Manager's unique PIN code (e.g. 6 digits).</summary>
    public string PinCode { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
