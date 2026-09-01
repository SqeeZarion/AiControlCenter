# AiControlCenter.Identity.Domain

Чистий домен Identity зберігає invariants користувачів, ролей і refresh sessions без залежностей від ASP.NET Core, EF Core або зовнішніх SDK.

## Модель

```mermaid
erDiagram
    USER ||--o{ USER_ROLE : has
    ROLE ||--o{ USER_ROLE : assigned
    USER ||--o{ REFRESH_SESSION : owns
    REFRESH_SESSION ||--o{ REFRESH_TOKEN : contains
    REFRESH_TOKEN o|--o| REFRESH_TOKEN : replaces
```

## Основні типи

- `User` — email, status, password hash, `MustChangePassword`, roles і login/lockout state.
- `Role` — системні ролі `Admin`, `Developer`, `User`.
- `UserRole` — зв’язок many-to-many.
- `RefreshSession` — stable login aggregate, owner, absolute expiry, client metadata та revocation state.
- `RefreshToken` — hash, one-time `UsedAt`, expiry та replacement link.
- Value objects нормалізують/валідують email, role name і refresh-token hash.

Домен не генерує JWT, не хешує пароль і не працює з БД. `ProjectReference` і NuGet-залежності відсутні.

[Identity](../README.md) · [Application](../AiControlCenter.Identity.Application/README.md) · [Unit tests](../../../../tests/Unit/AiControlCenter.Identity.UnitTests/README.md)
