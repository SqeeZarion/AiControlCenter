# Сервіси

Кожен сервіс володіє своїм кодом і даними. Прямі посилання на Domain/Application/Infrastructure іншого сервісу заборонені.

```mermaid
flowchart LR
    Gateway --> Identity
    Gateway --> ControlPlane
    Gateway --> Orchestrator
    Gateway --> Integrations
    Orchestrator -->|"gRPC"| ControlPlane
    Orchestrator -.-> RabbitMQ
    Worker -.-> RabbitMQ
```

- [Identity](Identity/README.md)
- [ControlPlane](ControlPlane/README.md)
- [Orchestrator](Orchestrator/README.md)
- [Integrations](Integrations/README.md)
- [Worker](Worker/AiControlCenter.Worker.Service/README.md)

[Межі сервісів](../../docs/architecture/service-boundaries.md) · [Головний README](../../README.md)
