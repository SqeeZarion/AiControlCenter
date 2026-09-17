# AiControlCenter.Orchestrator.UnitTests

Focused unit tests перевіряють `AgentRun` і `RunStep` без PostgreSQL, RabbitMQ або ASP.NET Core: допустимі переходи станів, монотонність timestamps, порядок кроків, bounded logs/results, message ownership та idempotent повтори progress.

```powershell
dotnet test .\tests\Unit\AiControlCenter.Orchestrator.UnitTests\AiControlCenter.Orchestrator.UnitTests.csproj --configuration Release
```

[Orchestrator](../../../src/Services/Orchestrator/README.md) · [Усі тести](../../README.md)
