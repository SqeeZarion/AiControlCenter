# AiControlCenter.Integrations.Api

ASP.NET Core host надає захищений `/service-info`, health endpoints і composition root Integrations.

```mermaid
flowchart LR
    Gateway -->|"Bearer token"| Api["Integrations.Api"]
    Api -->|"AnyPlatformUser AND PasswordChanged"| ServiceInfo["/service-info"]
    Api --> Infra["Integrations.Infrastructure"]
    Infra --> Db["PostgreSQL integrations"]
```

API повторно перевіряє JWT після Gateway. Readiness використовує `IntegrationsDbContext`; liveness залишається анонімним. Зовнішніх provider endpoints, clients або credentials у host немає.

Посилається на Application, Infrastructure, Observability і Security. Використовує FluentValidation, EF health і Swagger/OpenAPI.

[Integrations](../README.md) · [Security](../../../BuildingBlocks/AiControlCenter.Security/README.md) · [Integration tests](../../../../tests/Integration/AiControlCenter.Services.IntegrationTests/README.md)
