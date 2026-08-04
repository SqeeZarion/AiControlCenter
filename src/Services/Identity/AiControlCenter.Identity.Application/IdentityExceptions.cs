namespace AiControlCenter.Identity.Application;

public abstract class IdentityApplicationException(string message) : Exception(message);

public sealed class IdentityAuthenticationException()
    : IdentityApplicationException("Authentication failed.");

public sealed class IdentityForbiddenException(string message)
    : IdentityApplicationException(message);

public sealed class IdentityNotFoundException(string resource)
    : IdentityApplicationException($"{resource} was not found.");

public sealed class IdentityConflictException(string message)
    : IdentityApplicationException(message);
