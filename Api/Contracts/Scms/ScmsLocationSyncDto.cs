namespace Api.Contracts.Scms;

/// <summary>Body of SCMS's POST /api/pos/locations/sync: POS's full branch list.</summary>
public class ScmsLocationSyncRequest
{
    public List<ScmsLocationItem> Locations { get; set; } = new();
}

public class ScmsLocationItem
{
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    /// <summary>Not stored in POS; SCMS doesn't need it, so it is always empty.</summary>
    public string Address { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

/// <summary>What happened when POS sent its branch list to SCMS.</summary>
public class ScmsLocationSyncResult
{
    public bool Success { get; set; }
    public int LocationCount { get; set; }
    public string Message { get; set; } = string.Empty;
}
