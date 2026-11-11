# AiControlCenter.Worker.Service

Stateless ASP.NET Core host із health endpoints, технічним `/service-info` і production MassTransit consumers. Він виконує bounded deterministic Test workflow або ordered AI routes через authenticated Integrations gRPC та передає progress/attempts до Orchestrator через versioned gRPC із deadline, cancellation і окремим API key.

## Поточний процес

```mermaid
flowchart LR
    RabbitMQ["RabbitMQ"] -->|"ExecuteTestAgentRunV1 / ExecuteAiAgentRunV1"| Consumer["Worker consumers"]
    Consumer -->|"Execute provider"| Integrations["Integrations gRPC"]
    Consumer -->|"Begin / Step / Attempt / Complete"| Orchestrator["Orchestrator gRPC"]
    Host --> Health["/health/live та /health/ready"]
```

Worker не має database context, provider credentials, private signing key і не приймає business HTTP requests. `/service-info` і health endpoints анонімні. Readiness перевіряє RabbitMQ. Consumer не виконує shell або довільний код: він переносить typed tool audit і versioned checkpoint між Integrations та Orchestrator, а allowlisted executor працює в Integrations. Перед `ReportAttempt` Worker перевіряє повний serialized checkpoint за спільною межею 65,536 UTF-8 bytes; oversized payload не надсилається, а Run контрольовано завершується помилкою. Resume відновлює confirmed tool results і attempt cursor, тому вже підтверджені tools не запускаються повторно. Після bounded retries стандартний MassTransit fault завершується в Orchestrator.

Архітектурно проєкт може посилатися лише на `Contracts`, `Grpc.Contracts`, `Observability`; NuGet transport — `MassTransit.RabbitMQ`.

```powershell
dotnet run --project .\src\Services\Worker\AiControlCenter.Worker.Service\AiControlCenter.Worker.Service.csproj
```

[Сервіси](../../README.md) · [Contracts](../../../BuildingBlocks/AiControlCenter.Contracts/README.md) · [Комунікація](../../../../docs/architecture/communication.md)

[AI providers, fallback і tools](../../../../docs/architecture/ai-providers.md)
