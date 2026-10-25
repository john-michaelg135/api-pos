namespace Applications.Interfaces;

public sealed record PortalPosUser(
    string Id,
    string Username,
    string FirstName,
    string LastName,
    string Email,
    string Role);

public interface IPortalUserDirectory
{
    Task<IReadOnlyList<PortalPosUser>> ListEligiblePosUsersAsync(CancellationToken cancellationToken);
    Task<PortalPosUser?> FindEligiblePosUserAsync(string userId, CancellationToken cancellationToken);
}
