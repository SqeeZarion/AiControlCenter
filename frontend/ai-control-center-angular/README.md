# AiControlCenter Angular

Односторінковий Angular-клієнт працює за тим самим origin, що й Nginx/Gateway. Він реалізує вхід, відновлення сесії, зміну тимчасового пароля, захист маршрутів, панелі напрямків і тестових агентів, запуск виконання та live-перегляд статусу через SignalR.

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

- `AuthStore` ініціалізує CSRF cookie, пробує refresh під час старту, зберігає користувача та координує паралельні refresh-запити. Невдалий runtime refresh очищає session і переводить на login один раз.
- `AccessTokenStore` тримає access token лише в оперативній пам’яті; `localStorage` і `sessionStorage` не використовуються.
- `authInterceptor` додає Bearer token лише до захищених same-origin API. Він координує один refresh, а staggered `401` для старого token повторює з уже актуальним token без другого refresh; `403` та auth endpoints refresh не запускають.
- `authGuard`, `passwordChangedGuard` і `roleGuard` контролюють маршрути клієнта; серверні policies залишаються остаточним захистом.
- `RealtimeService` підключається до `/hubs/system`, передає актуальний access token, викликає технічний `Ping` і приймає обмежені `RunStatusChanged` events. Після reconnect сторінка Run перечитує REST-джерело істини.
- `LoginComponent`, `ChangePasswordComponent`, `ShellComponent` та `ForbiddenComponent` формують наявний UI.
- `DirectionListComponent` показує loading/empty/error states, pagination, фільтри та дозволені дії; `DirectionFormComponent` надає reactive create/edit form з одним atomic update request. `DirectionApiService` звертається лише до same-origin Gateway route.
- `AgentListComponent` і `AgentFormComponent` підтримують пошук, фільтри, pagination, керування визначеннями та запуск активного Test agent відповідно до ролі.
- `RunListComponent` читає список через REST. `RunDetailComponent` виконує SignalR-triggered REST reconciliation, застосовує лише новіші revisions і відновлює кроки та журнал після reload/reconnect.

## Маршрути

| URL                    | Компонент і захист                                     |
| ---------------------- | ------------------------------------------------------ |
| `/login`               | `LoginComponent`, публічний                            |
| `/change-password`     | `ChangePasswordComponent`, `authGuard`                 |
| `/forbidden`           | `ForbiddenComponent`, `authGuard`                      |
| `/`                    | `ShellComponent`, `authGuard` + `passwordChangedGuard` |
| `/directions`          | список, усі platform roles після зміни пароля          |
| `/directions/new`      | створення, додатково `roleGuard(Admin)`                |
| `/directions/:id/edit` | редагування, додатково `roleGuard(Admin)`              |
| `/agents`              | список і запуск Test agent, усі platform roles         |
| `/agents/new`          | створення, `roleGuard(Admin, Developer)`               |
| `/agents/:id/edit`     | редагування, `roleGuard(Admin, Developer)`             |
| `/home`                | Operations Center: topology, health, queues і Runs     |
| `/runs`                | власні Runs; Admin бачить усі                          |
| `/runs/:id`            | деталі, кроки, журнал і live status                    |

## Запуск і перевірка

Із кореня репозиторію:

```powershell
npm.cmd ci --prefix .\frontend\ai-control-center-angular
npm.cmd start --prefix .\frontend\ai-control-center-angular
npm.cmd test --prefix .\frontend\ai-control-center-angular -- --watch=false
npm.cmd run build --prefix .\frontend\ai-control-center-angular -- --configuration production
```

Тести перевіряють ініціалізацію/refresh сесії, memory-only token, збереження активної сесії під час transient outage, захисні guards, production `RealtimeService` через injectable SignalR factory, bounded reconnect/polling, single event subscription, API mapping Directions/Agents/Runs/Operations і REST reconciliation після SignalR event або reconnect. Окремий Kestrel integration test зупиняє та відновлює Gateway на тому самому endpoint і підтверджує automatic reconnect того самого SignalR client без ручного створення нового connection.

## Пов’язана документація

- [Головний README](../../README.md)
- [Повний authentication flow](../../docs/identity/authentication-flow.md)
- [Спільна JWT-безпека](../../docs/security/security-overview.md)
- [Gateway](../../src/Gateway/AiControlCenter.Gateway/README.md)
- [Directions](../../docs/architecture/directions.md)
- [Agents, Runs і Worker](../../docs/architecture/agents-runs-worker.md)
- [Operations Center і live observability](../../docs/architecture/operations-center.md)
