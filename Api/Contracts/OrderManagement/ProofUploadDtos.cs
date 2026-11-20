namespace Api.Contracts.OrderManagement;

/// <summary>POST orders/{orderId}/proof-uploads/{kind}: what the picker is about to upload.</summary>
public class ProofUploadRequestDto
{
    public string? Filename { get; set; }
    public string ContentType { get; set; } = string.Empty;
    /// <summary>Exact byte length of the JPEG Blob that will be PUT.</summary>
    public long Size { get; set; }
}

public class ProofUploadStartedDto
{
    /// <summary>Signed storage address: PUT the raw JPEG here with the same Content-Type and no credentials.</summary>
    public string UploadUrl { get; set; } = string.Empty;
    public int UploadUrlExpiresInSeconds { get; set; }
    /// <summary>Send back to .../confirm once the PUT succeeded.</summary>
    public string PendingReference { get; set; } = string.Empty;
}

/// <summary>POST orders/{orderId}/proof-uploads/{kind}/confirm.</summary>
public class ProofUploadConfirmDto
{
    public string PendingReference { get; set; } = string.Empty;
}

public class ProofUploadReadyDto
{
    /// <summary>Submit as <c>proofReference</c> with the delivery confirmation or refund request.</summary>
    public string ProofReference { get; set; } = string.Empty;
    public int ProofReferenceExpiresInSeconds { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
}
