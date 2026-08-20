# ControlPlane

ControlPlane має ізольований HTTP/gRPC host і власну PostgreSQL-схему. Зараз він надає лише захищений технічний `ServiceInfo`; доменні use cases та entities відсутні.

```mermaid
flowchart LR
    Gateway -->|"HTTP"| Api["ControlPlane.Api"]
    Orchestrator -->|"gRPC"| Api
    Api --> Application
    Api --> Infrastructure
    Application --> Domain
    Infrastructure --> Domain
    Infrastructure --> Db["PostgreSQL control_plane"]
```

- [Api](AiControlCenter.ControlPlane.Api/README.md)
- [Application](AiControlCenter.ControlPlane.Application/README.md)
- [Domain](AiControlCenter.ControlPlane.Domain/README.md)
- [Infrastructure](AiControlCenter.ControlPlane.Infrastructure/README.md)

[Межі сервісів](../../../docs/architecture/service-boundaries.md)
