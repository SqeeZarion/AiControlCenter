# AiControlCenter.Identity.Api

ASP.NET Core host приймає Identity HTTP-запити, застосовує transport/security middleware і викликає `IIdentityApplicationService`.

## Потік запиту

```mermaid
flowchart LR
    Client --> Filters["Origin, antiforgery, rate limit"]
    Filters --> Endpoint["IdentityEndpointExtensions"]
    Endpoint --> App["IIdentityApplicationService"]
    App --> Infra["Repositories та token services"]
    Infra --> Db["PostgreSQL identity"]
```

## Endpoints і компоненти

- Anonymous: `/v1/auth/csrf`, `/login`, `/refresh`, `/logout`.
- Authenticated: `/v1/auth/logout-all`, `/v1/users/me`, `/v1/users/me/change-password`.
- `AdminOnly` + `PasswordChanged`: users, status, roles, password reset і session revocation; `/v1/roles`.
- `IdentityExceptionHandler` переводить application/domain помилки у Problem Details.
- Startup перевіряє конфігурацію, застосовує migrations, seed системних ролей і bootstrap admin.

## Залежності й конфігурація

Посилається на `Identity.Application`, `Identity.Infrastructure`, `Observability`, `Security`. Використовує ASP.NET Core Identity Data Protection, EF health check, FluentValidation, OpenAPI/Swagger. Потрібні секції Identity database, authentication/RSA, refresh cookie, CORS/origin і bootstrap.

[Identity](../README.md) · [Application](../AiControlCenter.Identity.Application/README.md) · [Authentication flow](../../../../docs/identity/authentication-flow.md)
