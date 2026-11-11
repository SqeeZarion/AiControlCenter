# ADR 0008: AI provider boundaries, fallback і secret ownership

## Статус

Прийнято для Stage 5.

## Контекст

AgentRun має працювати з GitHub Copilot, OpenAI, xAI та локальним deterministic provider без залежності domain/application layers від конкретного SDK. Credentials належать конкретному користувачу; RabbitMQ має at-least-once delivery, а зовнішній timeout не доводить, що провайдер не виконав платний request.

## Рішення

- `Integrations` є єдиним owner provider connections, OAuth state, encrypted credentials і execution receipts. GitHub SDK та HTTP DTO не виходять за Infrastructure adapter boundary.
- ControlPlane зберігає лише декларативний ordered logical `ModelRoute`; при запуску Orchestrator просить Integrations owner-scoped server-side resolution і зберігає trusted immutable Run snapshot. Connection ID автора Agent ніколи не стає глобальним binding.
- Worker отримує trusted connection ID, але не secret, і викликає Integrations через authenticated gRPC. Integrations повторно звіряє owner та persisted provider. Універсальний `IAiProvider` повертає typed classification, request ID, actual model, usage/cost source, latency, finish status і bounded tool calls/results.
- Для paid provider atomic reservation створюється до generation у serializable transaction; pending/ambiguous cost входить у наступні перевірки. Completed receipt replay-иться; ambiguous outcome забороняє blind retry/fallback.
- Fallback є allowlist policy. Authentication, validation, safety, cancellation, tool/configuration errors, local budget і ambiguous timeout його не запускають. Paid execution і paid fallback потребують окремих явних flags та caps.
- Resume створює нову command із persisted versioned/hash-verified checkpoint; attempt cursor, confirmed tool results та immutable route snapshot не скидаються.
- Provider adapters лише декларують/декодують tool calls. Allowlisted executor знаходиться в application boundary, а typed audit проходить gRPC/RabbitMQ checkpoint path.
- OpenAI/xAI balance позначається `Unavailable`, якщо provider API не дає authoritative value. Локальний pricing catalog має source `Estimated`; AiControlCenter не є гаманцем.

## Наслідки

User isolation і secret boundary залишаються в одному сервісі, duplicate delivery не створює другого provider request після completed receipt, а невизначений timeout завершується fail-closed. Модель не може запросити shell/URL/path: Stage 5 tools обмежені server-side allowlist.

Поточний transport виконує bounded unary attempt. Provider capability metadata не оголошує streaming для adapter, доки end-to-end checkpointed streaming не реалізований і не перевірений. Це свідомо не імітує підтримку SDK/API.

## Verification boundary

Automated tests використовують `DeterministicTestProvider`, controlled Copilot runtime double та OpenAI/xAI HTTP stubs. Live Copilot smoke потребує окремої user authorization і не є автоматичним. Live OpenAI/xAI billing запити заборонені в Stage 5 verification.
