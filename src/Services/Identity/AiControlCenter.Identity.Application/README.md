# AiControlCenter.Identity.Application

Application layer оркеструє Identity use cases через доменні моделі та порти інфраструктури. Він не знає про HTTP, EF Core або конкретні криптографічні бібліотеки.

## Основний потік refresh

```mermaid
flowchart LR
    Refresh["RefreshAsync"] --> Hash["Hash token"]
    Hash --> Lock["Load for update"]
    Lock --> Validate["Validate session and user"]
    Validate --> Rotate["Revoke old and add replacement"]
    Rotate --> Commit["Transactional save"]
```

## Компоненти

- `IdentityApplicationService` реалізує login, refresh, logout, password і user/role administration.
- `Ports` оголошують repositories, unit of work, password hasher, refresh-token generator/hasher та access-token issuer.
- Request/response DTO і FluentValidation validators задають application contract.
- Транзакції захищають rotation refresh token і правило останнього активного Admin.

`ProjectReference`: `Identity.Domain`. NuGet: `FluentValidation` і DI extensions.

[Identity](../README.md) · [Domain](../AiControlCenter.Identity.Domain/README.md) · [Infrastructure](../AiControlCenter.Identity.Infrastructure/README.md) · [Як читати flow](../../../../docs/identity/authentication-flow.md)
