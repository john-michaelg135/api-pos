namespace Applications.Interfaces;

public interface IPosStaffIdentity
{
    /// <summary>The signed-in user's staff number (registered on first use), or null with no signed-in user.</summary>
    Task<int?> CurrentStaffIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Display names for staff numbers. Unknown numbers are left out.</summary>
    Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> staffIds, CancellationToken cancellationToken = default);
}
