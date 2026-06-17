namespace Applications.Interfaces;

public interface IXenditService
{
    /// <summary>
    /// Creates a Xendit invoice and returns the hosted invoice URL.
    /// </summary>
    /// <param name="successRedirectUrl">Optional override for the success redirect URL. Falls back to XENDIT_REDIRECT_URL env var.</param>
    /// <param name="failureRedirectUrl">Optional override for the failure redirect URL. Falls back to XENDIT_REDIRECT_URL env var.</param>
    Task<string> CreateInvoiceAsync(
        string orderNumber,
        decimal amount,
        string description,
        string? successRedirectUrl = null,
        string? failureRedirectUrl = null);
}
