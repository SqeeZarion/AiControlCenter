# AiControlCenter Angular

Односторінковий Angular-клієнт працює за тим самим origin, що й Nginx/Gateway. Він реалізує вхід, відновлення сесії, зміну тимчасового пароля, захист маршрутів і технічне SignalR-підключення.

## Потік сесії

```mermaid
sequenceDiagram
    participant App as Angular
    participant Gateway
    participant Identity
    App->>Gateway: POST /api/identity/v1/auth/login
    Gateway->>Identity: проксіює login
    Identity-->>App: access token та HttpOnly refresh cookie
    App->>Gateway: Bearer access token
    Gateway-->>App: захищений результат
    App->>Gateway: POST /api/identity/v1/auth/refresh
    Gateway->>Identity: cookie та X-XSRF-TOKEN
    Identity-->>App: новий access token і rotated cookie
```

## Основні компоненти

- `AuthStore` ініціалізує CSRF cookie, пробує refresh під час старту, зберігає користувача та координує паралельні refresh-запити.
- `AccessTokenStore` тримає access token лише в оперативній пам’яті; `localStorage` і `sessionStorage` не використовуються.
- `authInterceptor` додає Bearer token лише до захищених same-origin API та один раз повторює запит після успішного refresh.
- `authGuard`, `passwordChangedGuard` і `roleGuard` контролюють маршрути клієнта; серверні policies залишаються остаточним захистом.
- `RealtimeService` підключається до `/hubs/system`, передає актуальний access token і викликає технічний `Ping`.
- `LoginComponent`, `ChangePasswordComponent`, `ShellComponent` та `ForbiddenComponent` формують наявний UI.

## Маршрути

| URL                | Компонент і захист                                     |
| ------------------ | ------------------------------------------------------ |
| `/login`           | `LoginComponent`, публічний                            |
| `/change-password` | `ChangePasswordComponent`, `authGuard`                 |
| `/forbidden`       | `ForbiddenComponent`, `authGuard`                      |
| `/`                | `ShellComponent`, `authGuard` + `passwordChangedGuard` |

## Запуск і перевірка

Із кореня репозиторію:

```powershell
npm.cmd ci --prefix .\frontend\ai-control-center-angular
npm.cmd start --prefix .\frontend\ai-control-center-angular
npm.cmd test --prefix .\frontend\ai-control-center-angular -- --watch=false
npm.cmd run build --prefix .\frontend\ai-control-center-angular -- --configuration production
```

Тести перевіряють ініціалізацію/refresh сесії, memory-only token, захисні guards та поведінку interceptor без циклу повторних refresh.

## Пов’язана документація

- [Головний README](../../README.md)
- [Повний authentication flow](../../docs/identity/authentication-flow.md)
- [Спільна JWT-безпека](../../docs/security/security-overview.md)
- [Gateway](../../src/Gateway/AiControlCenter.Gateway/README.md)
