# Service boundaries

## Gateway

Public entry point for REST routes and the technical SignalR hub. It does not
own data or reference service Domain, Application or Infrastructure projects.
Authentication and authorization are deferred to Stage 2.

## Identity

Owns the `identity` PostgreSQL schema. Stage 1 contains only the service host,
layer boundaries and database connectivity. Users, roles, JWT and refresh
tokens are not implemented.

## ControlPlane

Owns the `control_plane` schema and exposes the versioned technical
`ServiceInfo` gRPC service. Directions, agents, workflows and schedules are
not implemented.

## Orchestrator

Owns the `orchestrator` schema. It has a direct gRPC client to ControlPlane and
a MassTransit bus connection. Runs, state machines and production messages are
deferred to Stage 4.

## Worker

Stateless BackgroundService host with MassTransit, observability, liveness and
readiness. It has no database and no business consumers in Stage 1.

## Integrations

Owns the `integrations` schema. External API adapters, OAuth accounts and
provider SDKs are deliberately deferred.

## Data ownership

The Development environment uses one PostgreSQL server, but each data owner
has a dedicated schema, login role and connection string. A role receives no
privileges on another service's schema. Cross-service reads must use versioned
REST, gRPC or message contracts.
