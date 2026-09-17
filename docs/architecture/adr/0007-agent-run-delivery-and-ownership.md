# ADR 0007: Власність AgentRun і at-least-once delivery

Статус: прийнято.

## Контекст

HTTP-запит має надійно створити Run і поставити його в асинхронну чергу. RabbitMQ може доставити command повторно, Worker може впасти між gRPC-викликами, а браузер може втратити SignalR connection.

## Рішення

- ControlPlane володіє AgentDefinition; Orchestrator отримує runnable snapshot через authenticated versioned gRPC.
- Orchestrator одноосібно володіє AgentRun/RunStep та всіма переходами.
- EF transactional bus outbox атомарно зберігає Run і outgoing messages.
- RabbitMQ доставка є at least once. Stable message ID, row lock і idempotent domain transitions роблять повторну обробку безпечною для persisted state.
- Worker stateless, не має БД, виконує лише детермінований Test workflow і передає progress через authenticated gRPC.
- SignalR event є bounded invalidation notification; клієнт відновлює стан із REST.

## Наслідки

Втрата повідомлення після DB commit усунена, але зовнішня дія в майбутніх execution types потребуватиме власного idempotency key. Exactly-once не гарантується. Історичний snapshot збільшує рядок Run, зате зберігає аудит після змін каталогу.
