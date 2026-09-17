using AiControlCenter.ControlPlane.Domain;

namespace AiControlCenter.ControlPlane.Application;

//Цей файл описує внутрішні повідомлення Application-рівня для роботи з агентами

//Використовується, коли сторінка «Агенти» завантажує список.
public sealed record ListAgentDefinitionsQuery(
    bool IncludeArchived = false,
    AgentDefinitionStatus? Status = null,
    Guid? DirectionId = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20)
{
    public const int MaximumPageSize = 100;
}

//внутрішня команда: створити нового агента.
public sealed record CreateAgentDefinitionCommand(
    Guid DirectionId,
    string Name,
    string Code,
    string? Description,
    AgentDefinitionStatus Status,
    AgentExecutionType ExecutionType);

//Команда змінює всі основні параметри агента:
public sealed record UpdateAgentDefinitionCommand(
    Guid DirectionId,
    string Name,
    string Code,
    string? Description,
    AgentDefinitionStatus Status,
    AgentExecutionType ExecutionType,
    uint Version);

//Активує або деактивує агента.
public sealed record ChangeAgentDefinitionStatusCommand(AgentDefinitionStatus Status, uint Version);
//Переміщує агента в архів без фізичного видалення з бази.
public sealed record ArchiveAgentDefinitionCommand(uint Version);
//Відновлює агента з архіву.
public sealed record RestoreAgentDefinitionCommand(uint Version);

//DTO — це безпечне представлення агента, яке Application-рівень повертає API, а API віддає Angular.
public sealed record AgentDefinitionDto(
    Guid Id,
    Guid DirectionId,
    string Name,
    string Code,
    string? Description,
    AgentDefinitionStatus Status,
    AgentExecutionType ExecutionType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    uint Version)
{
    public bool IsArchived => ArchivedAt is not null;
}

public sealed record AgentDefinitionListDto(
    IReadOnlyCollection<AgentDefinitionDto> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}
