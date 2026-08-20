# Repository guidance

## Structure

- `src/Gateway` — public API gateway and technical SignalR hub.
- `src/Services` — Identity, ControlPlane, Orchestrator, Worker and Integrations.
- `src/BuildingBlocks` — transport contracts, technical observability and shared security defaults only.
- `frontend` — Angular shell and Nginx configuration.
- `tests` — architecture, unit and integration tests.
- `docs/architecture` — service boundaries and communication decisions.

## Dependency rules

- Domain has no dependencies on Application, Infrastructure, ASP.NET Core, EF Core, MassTransit or external SDKs.
- Application may reference its own Domain.
- Infrastructure may reference its own Application and Domain.
- API may reference its own Application and Infrastructure.
- Services must not reference another service's Domain, Application or Infrastructure.
- Gateway may reference Contracts, Grpc.Contracts, Observability and Security only.
- Worker may reference Contracts, Grpc.Contracts and Observability only.

Do not implement planned functionality without an explicit user request. Never add credentials, tokens or private keys to Git.

## Verification

```powershell
dotnet restore .\AiControlCenter.sln
dotnet build .\AiControlCenter.sln --configuration Release --no-restore
dotnet test .\AiControlCenter.sln --configuration Release --no-build
npm.cmd ci --prefix .\frontend\ai-control-center-angular
npm.cmd run build --prefix .\frontend\ai-control-center-angular -- --configuration production
docker compose config
docker compose up --build --detach --wait
docker compose down
```

## Definition of Done

The requested scope is complete only when dependency rules hold, build and relevant tests pass, health checks are configured, Docker and `.env.example` are current, documentation matches the implementation, and no secrets are tracked.
