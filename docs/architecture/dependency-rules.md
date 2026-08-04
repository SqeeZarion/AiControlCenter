# Dependency rules

Allowed references inside a service:

```text
Domain          <- Application
Domain          <- Infrastructure
Application     <- Infrastructure
Application     <- Api
Infrastructure  <- Api
```

- Domain has no project dependencies and no EF Core, ASP.NET Core,
  MassTransit or external SDK packages.
- Application references only its own Domain.
- Infrastructure references only its own Application and Domain.
- API references its own Application and Infrastructure plus technical
  BuildingBlocks where needed.
- Gateway may reference Contracts, Grpc.Contracts, Observability and the
  technical Security building block, but not service layers.
- Worker may reference Contracts, Grpc.Contracts and Observability, but not
  service layers.
- A service may not reference another service's Domain, Application or
  Infrastructure.
- BuildingBlocks contain transport contracts and technical defaults, never
  shared domain entities. `AiControlCenter.Security` is limited to JWT
  validation, claim/policy names, SignalR token extraction and endpoint
  authorization helpers.

`AiControlCenter.ArchitectureTests` validates project references and banned
package references directly from the project files, including empty layers.
