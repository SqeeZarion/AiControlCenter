using AiControlCenter.ControlPlane.Domain;

namespace AiControlCenter.ControlPlane.Application;

public sealed record ListDirectionsQuery(
    bool IncludeArchived = false,
    DirectionStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20)
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
}

public sealed record GetDirectionQuery(Guid Id);

public sealed record CreateDirectionCommand(
    string Name,
    string Code,
    string? Description,
    string? Icon,
    DirectionStatus Status,
    int SortOrder);

public sealed record UpdateDirectionCommand(
    string Name,
    string Code,
    string? Description,
    string? Icon,
    DirectionStatus Status,
    int SortOrder,
    uint Version);

public sealed record ChangeDirectionStatusCommand(DirectionStatus Status, uint Version);

public sealed record ChangeDirectionSortOrderCommand(int SortOrder, uint Version);

public sealed record ArchiveDirectionCommand(uint Version);

public sealed record RestoreDirectionCommand(uint Version);

public sealed record DirectionDto(
    Guid Id,
    string Name,
    string Code,
    string? Description,
    string? Icon,
    DirectionStatus Status,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    uint Version)
{
    public bool IsArchived => ArchivedAt is not null;
}

public sealed record DirectionListDto(
    IReadOnlyCollection<DirectionDto> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}
