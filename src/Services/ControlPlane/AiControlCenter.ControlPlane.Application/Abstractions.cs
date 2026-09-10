using AiControlCenter.ControlPlane.Domain;

namespace AiControlCenter.ControlPlane.Application;

public interface IDirectionRepository
{
    Task<DirectionPage> ListAsync(
        ListDirectionsQuery query,
        CancellationToken cancellationToken);

    Task<Direction?> GetByIdAsync(Guid id, bool tracking, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken);

    void Add(Direction direction);
}

public sealed record DirectionPage(IReadOnlyCollection<Direction> Items, int TotalCount);

public interface IControlPlaneUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
