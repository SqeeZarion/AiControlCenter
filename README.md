# AiControlCenter

`AiControlCenter` — запланована адміністративна панель для керування
AI-агентами, автоматизаціями, інтеграціями та довготривалими фоновими
операціями.

> Етапи 0–1 реалізують лише технічний фундамент. Пунктирні потоки на схемі
> показують майбутню бізнес-взаємодію Етапу 4, а не наявні Agent/Run сценарії.

## Реалізований фундамент

- .NET 10 solution із Gateway та п'ятьма сервісними межами;
- Clean Architecture project references без міжсервісних залежностей;
- YARP маршрутизація, технічний SignalR hub і `ServiceInfo` gRPC v1;
- EF Core connectivity для чотирьох окремих PostgreSQL схем і ролей;
- RabbitMQ/MassTransit hosts без production messages або consumers;
- Problem Details, correlation ID, JSON logging, OpenTelemetry і health checks;
- Angular 22 shell, Nginx, Docker Compose, тести та CI.

## Взаємодія компонентів

```mermaid
flowchart LR
    B["Browser"] --> F["Angular / frontend Nginx"]
    F -->|"/api, /hubs"| G["Gateway"]

    G -->|"REST routing"| ID["Identity API"]
    G -->|"REST routing"| CP["ControlPlane API"]
    G -->|"REST routing"| OR["Orchestrator API"]
    G -->|"REST routing"| IN["Integrations API"]

    OR -->|"gRPC ServiceInfo; future configuration lookup"| CP

    OR -.->|"Stage 4: persist state, then send command"| MQ["RabbitMQ"]
    MQ -.->|"Stage 4: consume command"| W["Worker Service"]
    W -.->|"Stage 4: result event"| MQ
    MQ -.->|"Stage 4: result to Orchestrator"| OR
    OR -.->|"Stage 4: persist new state, then publish event"| MQ
    MQ -.->|"Stage 4: state event"| G

    G -->|"SignalR technical connection; business updates in Stage 4"| B

    ID --> PG[("PostgreSQL")]
    CP --> PG
    OR --> PG
    IN --> PG
```

### Основний маршрут запиту

1. Користувач відкриває Angular-застосунок у браузері.
2. Frontend надсилає API-запити на `/api` і підключається до SignalR через
   `/hubs`.
3. Gateway є єдиною публічною API-точкою та маршрутизує REST-запит до
   потрібного внутрішнього сервісу.
4. Внутрішні сервіси не викликають один одного через Gateway. Для коротких
   синхронних операцій вони використовують прямі gRPC-виклики всередині
   Docker-мережі.

### Відповідальність сервісів

- **Gateway** — зовнішня точка входу, REST-маршрутизація, SignalR та, після
  реалізації авторизації, перевірка доступу.
- **Identity API** — майбутній власник користувачів, ролей і токенів.
- **ControlPlane API** — майбутній власник конфігурації Directions, Agents,
  Workflows і Schedules.
- **Orchestrator API** — майбутній власник стану Runs і координації
  виконання.
- **Worker Service** — майбутнє фонове виконання довготривалих завдань поза
  HTTP-запитом.
- **Integrations API** — ізольований доступ до зовнішніх платформ та API.

### REST і gRPC

REST використовується між frontend і Gateway та для зовнішніх API. gRPC
призначений для короткої синхронної взаємодії між внутрішніми .NET-сервісами,
коли відповідь потрібна одразу.

Основний напрямок — `Orchestrator → ControlPlane`: Orchestrator отримуватиме
конфігурацію агента або workflow з ControlPlane. Довготривалу роботу через gRPC запускати не слід,
оскільки вона не повинна утримувати відкритий HTTP-запит.

### RabbitMQ і MassTransit

RabbitMQ є брокером асинхронних повідомлень, а MassTransit надає .NET-рівень
для надсилання команд, публікації подій, реєстрації consumers, повторних спроб
і health checks.

Майбутній асинхронний сценарій:

1. Orchestrator спочатку фіксує операцію у власній базі.
2. Через MassTransit він надсилає команду в RabbitMQ.
3. Worker отримує команду та виконує завдання у фоні.
4. Worker публікує подію з результатом або помилкою назад у RabbitMQ.
5. Orchestrator отримує результат і спочатку зберігає новий стан.
6. Лише після успішного збереження Orchestrator публікує подію зміни стану.
7. Gateway отримує цю подію та передає оновлення браузеру через SignalR.

RabbitMQ не гарантує, що повідомлення буде доставлене лише один раз, тому
майбутні consumers мають бути ідемпотентними. Для надійних бізнес-операцій
також знадобляться механізми outbox/inbox і дедуплікації.

### SignalR

SignalR використовується тільки для повідомлень у реальному часі: прогресу,
змін статусу та системних сповіщень. Він не є джерелом істини. Після
перепідключення frontend повинен отримати актуальний стан через REST, а
SignalR використовувати для наступних змін.

### PostgreSQL і володіння даними

У Development сервіси можуть використовувати один контейнер PostgreSQL, але
кожен сервіс володіє окремою схемою:

- `identity`;
- `control_plane`;
- `orchestrator`;
- `integrations`.

Сервіс не повинен напряму читати або змінювати таблиці іншого сервісу.
Обмін даними відбувається тільки через versioned REST API, gRPC-контракти або
message contracts. Gateway і Worker не отримують власні схеми, доки не
стають власниками окремих даних.

### Межі архітектурного фундаменту

Етапи 0–1 містять лише transport/configuration foundation: Gateway,
контракти, підключення PostgreSQL і RabbitMQ, MassTransit, технічний SignalR
hub, health checks, логування та Docker Compose. Production message contracts
і consumers для пунктирних потоків ще не створені.

Авторизація, Directions, Agents, Runs, Worker-бізнес-логіка, OpenAI та
зовнішні agent runtimes реалізуються окремими наступними етапами.

## Локальний запуск

Передумови: .NET SDK `10.0.302`, Node.js `24.x`, npm і Docker Compose.

```powershell
Copy-Item .env.example .env
dotnet restore .\AiControlCenter.sln
dotnet build .\AiControlCenter.sln --configuration Release --no-restore
dotnet test .\AiControlCenter.sln --configuration Release --no-build

npm.cmd ci --prefix .\frontend\ai-control-center-angular
npm.cmd run build --prefix .\frontend\ai-control-center-angular -- --configuration production

docker compose config
docker compose up --build --detach --wait
docker compose ps
```

Публічна адреса frontend: `http://localhost:8080`. Gateway доступний для
діагностики на `http://localhost:5080`; Swagger сервісів — на портах
`5101`, `5102`, `5104` і `5105`. RabbitMQ Management —
`http://localhost:15672`.

Після роботи:

```powershell
docker compose down
```

`.env` і локальні секрети ігноруються Git. Значення з `.env.example`
призначені лише як безпечні Development placeholders і мають бути замінені.
