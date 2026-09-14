using Api.Contracts.Scms;

namespace Applications.Interfaces;

/// <summary>
/// POS-side reconciliation between SCM stock-transfer manifests and the stock POS
/// actually received. Detects drift (short/over/unmatched/unreceived lines and
/// missed SCM status callbacks) and can heal a single transfer on demand.
/// </summary>
public interface IReconciliationService
{
    /// <summary>
    /// Build a reconciliation report across transfers. When <paramref name="onlyDiscrepancies"/>
    /// is true, only transfers with issues are returned in the detail list.
    /// </summary>
    Task<ReconciliationReportDto> BuildReportAsync(bool onlyDiscrepancies = false);

    /// <summary>
    /// Re-run matching + reconciliation for one transfer and re-attempt the SCM
    /// status callback if it hasn't been acknowledged. Returns what changed.
    /// </summary>
    Task<ReconciliationResyncResultDto> ResyncTransferAsync(string transferId);

    /// <summary>
    /// Background sweep: for every transfer that was received but whose SCM status
    /// callback is still Pending or Failed, re-attempt the callback. Returns the
    /// number of transfers that became Acknowledged in this pass.
    /// </summary>
    Task<int> RetryFailedStatusCallbacksAsync(CancellationToken cancellationToken = default);
}
