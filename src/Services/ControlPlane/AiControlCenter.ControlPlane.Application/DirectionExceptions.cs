namespace AiControlCenter.ControlPlane.Application;

public abstract class DirectionApplicationException(string message) : Exception(message);

public sealed class DirectionNotFoundException(Guid id)
    : DirectionApplicationException($"Direction '{id}' was not found.");

public sealed class DirectionConflictException(string message)
    : DirectionApplicationException(message);
