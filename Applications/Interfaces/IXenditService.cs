namespace Applications.Interfaces;

public interface IXenditService
{
    Task<string> CreateInvoiceAsync(string orderNumber, decimal amount, string description);
}
