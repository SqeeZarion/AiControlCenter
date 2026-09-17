# AiControlCenter.Grpc.Contracts

Class library генерує клієнтські та серверні C#-типи з versioned protobuf. Окрім technical `ServiceInfo`, він містить `AgentCatalog` snapshot і `RunProgress` для Worker.

## Основний процес

`ServiceInfo.GetServiceInfo` приймає `ServiceInfoRequest.caller` і повертає `service_name`, `version` та `environment`.

```mermaid
sequenceDiagram
    participant Orchestrator
    participant Contract as ServiceInfo contract
    participant ControlPlane
    Orchestrator->>ControlPlane: GetServiceInfo(caller)
    Note over Orchestrator,ControlPlane: Типи запиту й відповіді надає Grpc.Contracts
    ControlPlane-->>Orchestrator: name, version, environment
```

## Залежності

`AgentCatalog.GetRunnableAgent` повертає bounded immutable snapshot. `RunProgress.BeginRun`, `ReportStep` і `CompleteRun` не дають Worker доступу до Orchestrator DB; server перевіряє API key, порядок і state transitions.

- `Google.Protobuf`, `Grpc.Core.Api`.
- `Grpc.Tools` використовується лише під час build.
- `ProjectReference`: відсутні.

Зміна `.proto` є зміною transport contract і потребує перевірки обох сторін виклику.

[Building Blocks](../README.md) · [Комунікація](../../../docs/architecture/communication.md)
