namespace AiControlCenter.ControlPlane.Application;

public abstract class AgentDefinitionApplicationException(string message) : Exception(message);

public sealed class AgentDefinitionNotFoundException(Guid id)
    : AgentDefinitionApplicationException($"Agent definition '{id}' was not found.");

public sealed class AgentDefinitionConflictException(string message)
    : AgentDefinitionApplicationException(message);

public sealed class AgentDirectionNotFoundException(Guid id)
    : AgentDefinitionApplicationException($"Direction '{id}' was not found.");

public sealed class AgentNotRunnableException(string message)
    : AgentDefinitionApplicationException(message);

public sealed class AgentModelRouteValidationException(
    string code,
    string field,
    string message,
    int statusCode = 400) : AgentDefinitionApplicationException(message)
{
    public string Code { get; } = code;
    public string Field { get; } = field;
    public int StatusCode { get; } = statusCode;
}
