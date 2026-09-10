# AiControlCenter.ControlPlane.UnitTests

Focused unit tests перевіряють `Direction` без EF Core або ASP.NET Core: нормалізацію, boundaries, status/sort transitions, timestamps, archive/restore invariants і відповідність Application validators доменним правилам.

```powershell
dotnet test .\tests\Unit\AiControlCenter.ControlPlane.UnitTests\AiControlCenter.ControlPlane.UnitTests.csproj --configuration Release
```

[ControlPlane](../../../src/Services/ControlPlane/README.md) · [Усі тести](../../README.md)
