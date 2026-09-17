# Агенти, запуски та Worker

## Межі даних

`ControlPlane` володіє `AgentDefinition` і зв’язком агента з `Direction`. `Orchestrator` володіє `AgentRun`, `RunStep`, переходами стану та історією. Stateless `Worker` не має доступу до PostgreSQL: він отримує bounded command із RabbitMQ і звітує лише через versioned gRPC.

Run зберігає незмінний snapshot назви, коду й версії агента та напрямку. Тому історичний запуск не змінюється після редагування каталогу. JWT, refresh tokens, ключі та connection strings не входять у snapshot, повідомлення чи журнал.

## Матриця доступу

| Дія | Admin | Developer | User |
| --- | --- | --- | --- |
| Читати агенти | так | так | так |
| Створювати й змінювати агенти | так | так | ні |
| Запускати активний Test agent | так | так | так |
| Читати власні Runs | так | так | так |
| Читати чужі Runs | так | ні | ні |

Усі REST і delegated gRPC endpoints додатково вимагають `PasswordChanged`. Запит іншого користувача на конкретний Run повертає `404`, щоб не розкривати існування ресурсу. Angular guards лише керують UI; остаточне рішення завжди приймає backend.

## Запуск і transactional outbox

1. Gateway перевіряє Bearer token і через YARP передає `POST /v1/runs` Orchestrator.
2. Orchestrator передає цей token у metadata versioned gRPC-запиту `AgentCatalog.GetRunnableAgent`.
3. ControlPlane повторно перевіряє JWT і повертає snapshot тільки якщо Agent і Direction активні та не архівовані.
4. Orchestrator додає `Queued` Run, execution command і status event до одного EF Core commit. MassTransit bus outbox зберігає повідомлення в схемі `orchestrator`.
5. Outbox доставляє `ExecuteTestAgentRunV1` у RabbitMQ після успішного commit. Падіння процесу між commit і publish не втрачає завдання.

Доставка має семантику **at least once**. Повторна доставка можлива; exactly-once виконання не обіцяється. Стабільний `MessageId` закріплює Run за одним execution message. Orchestrator серіалізує progress через PostgreSQL `FOR UPDATE`, а повтор того самого переходу повертає `AlreadyApplied` без нової ревізії.

## Життєвий цикл

| Поточний стан | Дозволений наступний стан | Умова |
| --- | --- | --- |
| `Queued` | `Running` | Worker успішно claim-ить Run |
| `Queued` | `Failed` | невиправна помилка до старту або fault після retries |
| `Running` | `Succeeded` | є кроки й кожен завершився успішно |
| `Running` | `Failed` | контрольована або невиправна помилка |
| `Succeeded`, `Failed` | — | terminal status не змінюється |

Новий Step починається лише як `Running`, має наступний порядковий номер `1..16`, а попередній Step повинен бути `Succeeded`. Names, input, result, error і logs мають фіксовані межі. Timestamps не можуть рухатися назад.

## Worker і gRPC progress

Черга Worker має стабільне ім’я `aicontrolcenter-worker-agent-runs-v1`. Consumer виконує тільки три детерміновані дії: перевірку вводу, тестову дію та завершення. `ExpectedOutcome=Fail` створює контрольовану помилку; shell, зовнішні SDK та довільний код відсутні.

Worker викликає `RunProgress.BeginRun`, `ReportStep` і `CompleteRun` з deadline та cancellation. Внутрішній endpoint використовує окремий щонайменше 32-символьний API key, constant-time comparison і не приймає browser JWT. Ключ надходить лише з configuration і не входить до Git.

Після bounded retries MassTransit публікує `Fault<ExecuteTestAgentRunV1>`. Orchestrator споживає його зі стабільної черги `aicontrolcenter-orchestrator-run-faults-v1` і переводить незавершений Run у `Failed`. Стандартна MassTransit error queue зберігає повідомлення, якщо сам fault consumer не може завершити обробку.

## SignalR та відновлення стану

Після commit кожного застосованого переходу Orchestrator через outbox публікує bounded `RunStatusChangedV1`: Run ID, owner ID, status, revision і необов’язковий номер/status кроку. Повні logs, input і secrets у події відсутні.

Gateway consumer надсилає подію лише в серверні групи `runs:user:{sub}` та `runs:admins`. `SystemHub` формує взаємовиключне membership із перевірених JWT claims: Admin входить лише в `runs:admins`, а не-Admin — лише у власну `runs:user:{sub}`. Це не дає Admin-власнику отримати одну подію через дві групи; клієнт не може назвати довільний Run або чужий user ID. SignalR є повідомленням про зміну, а PostgreSQL/REST — джерелом істини. Angular приймає лише подію з новішою `revision`, перечитує деталі через REST і після reconnect робить повторне REST-завантаження.

## Контракти й маршрути

- ControlPlane REST: `/api/control-plane/v1/agents`, `/{id}`, `/{id}/status`, `/{id}/archive`, `/{id}/restore`.
- Orchestrator REST: `/api/orchestrator/v1/runs`, `/{id}`.
- gRPC: `AgentCatalog.GetRunnableAgent`; `RunProgress.BeginRun`, `ReportStep`, `CompleteRun`.
- Messages: `ExecuteTestAgentRunV1`, `RunStatusChangedV1`, стандартний `Fault<ExecuteTestAgentRunV1>`.

`v1` у route, protobuf package і CLR namespace є частиною versioning contract.

[Комунікація](communication.md) · [Межі сервісів](service-boundaries.md) · [ADR](adr/0007-agent-run-delivery-and-ownership.md)
