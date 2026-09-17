# AiControlCenter.Worker.Service

Stateless ASP.NET Core host із health endpoints, технічним `/service-info` і production MassTransit consumer. Він виконує тільки bounded deterministic Test workflow та передає progress до Orchestrator через versioned gRPC із deadline, cancellation і окремим API key.

## Поточний процес

```mermaid
flowchart LR
    RabbitMQ["RabbitMQ"] -->|"ExecuteTestAgentRunV1"| Consumer["TestAgentRunConsumer"]
    Consumer -->|"Begin / Step / Complete"| Orchestrator["Orchestrator gRPC"]
    Host --> Health["/health/live та /health/ready"]
```

Worker не має database context, private signing key і не приймає business HTTP requests. `/service-info` і health endpoints анонімні. Readiness перевіряє RabbitMQ. Consumer не виконує shell або довільний код; після bounded retries стандартний MassTransit fault завершується в Orchestrator.

Архітектурно проєкт може посилатися лише на `Contracts`, `Grpc.Contracts`, `Observability`; NuGet transport — `MassTransit.RabbitMQ`.

```powershell
dotnet run --project .\src\Services\Worker\AiControlCenter.Worker.Service\AiControlCenter.Worker.Service.csproj
```

[Сервіси](../../README.md) · [Contracts](../../../BuildingBlocks/AiControlCenter.Contracts/README.md) · [Комунікація](../../../../docs/architecture/communication.md)
