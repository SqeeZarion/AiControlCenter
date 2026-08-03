# Component communication

## Implemented in Stage 1

- Browser traffic enters through frontend Nginx.
- Nginx serves the Angular SPA and proxies `/api` and `/hubs` to Gateway.
- Gateway uses YARP to route REST calls to internal APIs.
- Gateway hosts a technical SignalR hub with a `Ping` method.
- Orchestrator can call the versioned ControlPlane `ServiceInfo` contract over
  direct HTTP/2 gRPC using Docker DNS.
- Orchestrator and Worker register MassTransit buses and RabbitMQ health
  checks, but no production messages or consumers.
- Data-owner APIs verify PostgreSQL connectivity in readiness checks.

## Deferred until Stage 4

The future durable flow is ordered as follows:

1. Orchestrator persists the initial state.
2. Orchestrator sends a versioned command through MassTransit/RabbitMQ.
3. Worker consumes and executes the command idempotently.
4. Worker publishes a result event back through RabbitMQ.
5. Orchestrator consumes the result and persists the new state.
6. Only after persistence succeeds, Orchestrator publishes a state event.
7. Gateway consumes the state event and sends a SignalR notification.
8. The browser treats SignalR as a hint and retrieves authoritative state by
   REST when needed.

Production messages, outbox/inbox, deduplication, AgentRun and progress events
do not exist in Stage 1. gRPC is not used to probe Worker availability; queue
and bus health are observed through RabbitMQ, MassTransit and telemetry.
