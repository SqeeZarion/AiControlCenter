namespace AiControlCenter.Orchestrator.Application;

public abstract class AgentRunApplicationException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class AgentRunNotFoundException(Guid id)
    : AgentRunApplicationException($"Agent run '{id}' was not found.");

public sealed class AgentRunAccessDeniedException()
    : AgentRunApplicationException("The requested run is not available to this user.");

public sealed class AgentCatalogUnavailableException(Exception inner)
    : AgentRunApplicationException("Agent catalog is unavailable.", inner);

public sealed class AgentRunAgentNotFoundException(Guid id)
    : AgentRunApplicationException($"Agent '{id}' was not found.");

public sealed class AgentRunConflictException(string message)
    : AgentRunApplicationException(message);
