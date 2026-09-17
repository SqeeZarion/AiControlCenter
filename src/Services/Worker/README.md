# Worker

Каталог містить stateless executable Worker для безпечного детермінованого Test workflow.

```mermaid
flowchart LR
    RabbitMQ --> Worker["AiControlCenter.Worker.Service"]
    Worker -->|"RunProgress gRPC"| Orchestrator
    Worker --> Health["Health endpoints"]
```

- [AiControlCenter.Worker.Service](AiControlCenter.Worker.Service/README.md)
- [Комунікація](../../../docs/architecture/communication.md)
