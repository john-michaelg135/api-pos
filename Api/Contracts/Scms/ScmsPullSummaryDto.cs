namespace Api.Contracts.Scms;

/// <summary>Summary returned after a SCMS pull-and-receive operation.</summary>
public class ScmsPullSummaryDto
{
    public int TotalDeliveriesReceived { get; set; }
    public int TotalItemsUpdated { get; set; }
    public int TotalErrors { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime PulledAt { get; set; }
}
