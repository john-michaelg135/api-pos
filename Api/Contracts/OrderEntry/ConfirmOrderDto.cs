namespace Api.Contracts.OrderEntry;

public class ConfirmOrderDto
{
    public int SubmittedBy { get; set; }
    public bool PrintReceipt { get; set; } = false;
}
