using Api.Contracts.Scms;

namespace Applications.Interfaces;

public interface IScmsIntegrationService
{
    // US-POS-028: Pull finished goods deliveries from the SCMS group's API
    // and automatically update local stock totals.
    Task<ScmsPullSummaryDto> PullAndReceiveDeliveriesAsync();
}
