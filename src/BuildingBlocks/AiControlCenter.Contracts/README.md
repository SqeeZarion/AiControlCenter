# AiControlCenter.Contracts

.NET class library для спільних versioned асинхронних transport contracts. Містить bounded `ExecuteTestAgentRunV1` та `RunStatusChangedV1`; entities й application use cases тут заборонені.

## Межі та місце в системі

Проєкт не залежить від сервісів і не повинен містити entities або application use cases. Його можуть посилати Gateway, Orchestrator і Worker без порушення меж сервісів.

```mermaid
flowchart LR
    Orchestrator["Orchestrator API"] --> Contracts["AiControlCenter.Contracts"]
    Worker["Worker"] --> Contracts
    Contracts --> Command["ExecuteTestAgentRunV1"]
    Contracts --> Event["RunStatusChangedV1"]
```

## Компоненти й залежності

- `ContractVersion` — узгоджене значення `v1`.
- `ProjectReference`: відсутні.
- NuGet-пакети: відсутні.
- Зовнішні сервіси: відсутні.

## Перевірка

```powershell
dotnet build .\src\BuildingBlocks\AiControlCenter.Contracts\AiControlCenter.Contracts.csproj --configuration Release
```

[Building Blocks](../README.md) · [Комунікація](../../../docs/architecture/communication.md)
