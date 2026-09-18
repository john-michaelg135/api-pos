namespace Api.Contracts.Scms;

public class BranchListResponseDto
{
    public List<BranchItemDto> Items { get; set; } = new();
}

public class BranchItemDto
{
    public string BranchId { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
