# Identity

Identity володіє користувачами, ролями, паролями й refresh sessions. Лише цей сервіс має приватний RSA-ключ та створює access tokens.

```mermaid
flowchart LR
    Api["Identity.Api"] --> Application["Identity.Application"]
    Api --> Infrastructure["Identity.Infrastructure"]
    Application --> Domain["Identity.Domain"]
    Infrastructure --> Application
    Infrastructure --> Domain
    Infrastructure --> PostgreSQL["PostgreSQL: identity"]
```

## Проєкти

- [Api](AiControlCenter.Identity.Api/README.md) — HTTP endpoints, middleware, CSRF/origin/rate limiting і composition root.
- [Application](AiControlCenter.Identity.Application/README.md) — login/refresh/logout, password і admin use cases.
- [Domain](AiControlCenter.Identity.Domain/README.md) — `User`, `Role`, `UserRole`, `RefreshToken`, value objects та invariants.
- [Infrastructure](AiControlCenter.Identity.Infrastructure/README.md) — EF Core, repositories, Argon2id, RSA JWT і Data Protection.

## Поточні можливості

Login, single-use refresh rotation/reuse detection, logout/logout-all, зміна або reset пароля, список/створення/блокування користувачів, ролі та відкликання сесій. Public endpoints захищені CSRF/origin перевірками й rate limiting там, де це налаштовано.

[Authentication flow](../../../docs/identity/authentication-flow.md) · [Identity security](../../../docs/architecture/identity-security.md)
