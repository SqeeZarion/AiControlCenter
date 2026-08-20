# AiControlCenter.Orchestrator.Infrastructure

Infrastructure реєструє `OrchestratorDbContext` і ізольовану PostgreSQL-схему. Entities, repositories та migrations зараз відсутні.

```mermaid
flowchart LR
    Api["Orchestrator.Api"] --> Infra["Orchestrator.Infrastructure"]
    Infra --> Application["Orchestrator.Application"]
    Infra --> Domain["Orchestrator.Domain"]
    Infra --> Db["schema orchestrator"]
```

`AddOrchestratorInfrastructure` налаштовує Npgsql та власну migration history table. RabbitMQ і gRPC налаштовані в API, а не в цьому проєкті.

Посилається на Application і Domain; використовує EF Core, Npgsql provider та configuration abstractions.

[Orchestrator](../README.md) · [Api](../AiControlCenter.Orchestrator.Api/README.md) · [Integration tests](../../../../tests/Integration/AiControlCenter.Services.IntegrationTests/README.md)
