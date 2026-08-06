namespace AiControlCenter.Identity.Application;

//обробка помилок і заборон
public abstract class IdentityApplicationException(string message) : Exception(message);

//неправильні дані
public sealed class IdentityAuthenticationException()
    : IdentityApplicationException("Authentication failed.");

public sealed class IdentityForbiddenException(string message)
    : IdentityApplicationException(message);

public sealed class IdentityNotFoundException(string resource)
    : IdentityApplicationException($"{resource} was not found.");

//Користувач відомий, але операція йому заборонена.
public sealed class IdentityConflictException(string message)
    : IdentityApplicationException(message);
