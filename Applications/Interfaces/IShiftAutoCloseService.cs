namespace Applications.Interfaces;

public interface IShiftAutoCloseService
{
    // Closes every shift still open after its business day ended; returns how many it closed
    Task<int> CloseOverdueShiftsAsync(CancellationToken cancellationToken = default);
}
