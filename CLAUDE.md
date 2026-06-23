# CLAUDE.md — Instructions for Claude Code

This repository is a learning playground for **RabbitMQ Streams** with two small,
production-leaning .NET services that talk through a stream.

## 🔴 Operating rules for Claude (read every session)

1. **Track progress in [PLAN.md](PLAN.md).** Every step has a `Status` and a
   `Result` field. When you start a step set it to `IN PROGRESS`, when you finish
   set it to `DONE` and fill in `Result` with what actually happened (commands run,
   errors hit, decisions made). Never silently skip a step.
2. **This file is the decision log.** If we change a decision (library version,
   retention policy, message format, topology, ports, anything in the
   "Key decisions" table below), update the table here **and** note the change in
   the relevant PLAN.md step's `Result`. Decisions and their rationale live here so
   future sessions don't re-litigate them.
3. **Comment the *why*, not the *what*, in code** — especially around RabbitMQ
   configuration and the publish/consume integration, since that is the focus of
   this project.
4. **Keep the apps trivial.** Business logic must stay minimal. Effort goes into
   the RabbitMQ integration (stream declaration, retention, publish confirms,
   offset tracking, reconnection, idempotency), not into customer/notification
   features.
5. **Production-leaning defaults.** Prefer configuration that you would not be
   embarrassed to ship: explicit retention, durable offset tracking, graceful
   shutdown, health checks, no hardcoded secrets sprinkled around (centralize in
   config / env).

## What we are building

A **real-but-simple** use case:

- **Customer Service** (publisher, `customer-service/`) — owns customer records.
  When a customer is updated it publishes a `CustomerUpdated` **snapshot** event to
  the stream `customer.events`.
- **Notification Service** (consumer, `notification-service/`) — keeps the contact
  info (email / phone) it would use to notify customers. It consumes
  `CustomerUpdated`, and **only when email or phone actually changed** it updates
  its local contact store and logs a "notification channel updated" line.

Two **separate solutions** on purpose — they are independent deployables that share
only the wire contract (a JSON message). The contract is intentionally duplicated
in each service (see "Key decisions").

## Topology

```
                 PUT /customers/{id}
   (you / curl) ───────────────────────▶  Customer Service ──┐
                                                              │ publish (stream protocol :5552)
                                                              ▼
                                                     ┌──────────────────┐
                                                     │  RabbitMQ Stream  │
                                                     │  "customer.events"│
                                                     └──────────────────┘
                                                              │ consume + server-side offset tracking
                                                              ▼
                                                     Notification Service
                                                     (updates contact store on email/phone change)
```

## Key decisions (the decision log — keep current)

| Topic | Decision | Why |
|---|---|---|
| Broker | RabbitMQ `4.x` (`-management` image) | Streams are GA and mature in 4.x; management UI helps learning. |
| Stream plugin | `rabbitmq_stream` + `rabbitmq_stream_management` enabled via `enabled_plugins` | Streams use a **dedicated binary protocol on :5552**, not AMQP. |
| .NET | `net8.0` | LTS, installed locally. |
| Client lib | `RabbitMQ.Stream.Client` (pinned in csproj) | Official stream-protocol client; not the AMQP `RabbitMQ.Client`. |
| Stream name | `customer.events` | One stream, snapshot events. |
| Retention | `MaxAge = 7d`, `MaxLengthBytes = 2 GB`, `MaxSegmentSizeBytes = 100 MB` | Streams are append-only logs — they **must** have a retention cap or they grow forever. Segment size bounds truncation granularity. |
| Message body | UTF-8 JSON `CustomerUpdated` snapshot | Simple, debuggable in the UI. Snapshot (full state) lets the consumer diff without event-history replay. |
| Contract sharing | Duplicated `CustomerUpdated` record in each solution | Services are separate deployables; in real life this would be a shared NuGet "contracts" package. Documented, not accidental. |
| Idempotency / ordering | `Version` field per customer; consumer ignores stale/duplicate versions | Streams give at-least-once delivery on redelivery; consumer must be idempotent. |
| Offset tracking | **Server-side**, keyed by consumer `Reference = "notification-service"` | Survives consumer restarts without an external store; the stream remembers where we were. |
| Publisher reliability | Reliable `Producer` with a confirmation handler | Confirms prove the broker persisted the message; we log unconfirmed sends. |
| Credentials | `app` / `app-pass` via env in compose; same in app config | Avoid the `guest` user (loopback-only, not for apps). Real prod would use secrets, not committed values. |
| Stream host advertise | `stream.advertised_host = localhost` | The broker hands clients the node hostname to connect to; without this a host-side client gets the unreachable container hostname. Classic streams gotcha. |
| Container engine | Works with **Docker or Podman** (`run.sh` auto-detects; override with `CONTAINER_ENGINE`) | Some environments restrict Docker. The compose file is shared; config mounts use `:ro,z` (SELinux relabel — required by rootless Podman, ignored by Docker). Readiness is checked via `rabbitmq-diagnostics` inside the container, not the engine's health JSON, because Docker (`.State.Health`) and Podman (`.State.Healthcheck`) expose it differently. |

## Ports

| Port | Use |
|---|---|
| 5552 | Stream protocol (apps connect here) |
| 5672 | AMQP (not used by the apps; available) |
| 15672 | Management UI — http://localhost:15672 (`app` / `app-pass`) |

## How to run

> Quickest path: `./run.sh` (auto-detects Docker or Podman, starts everything and
> sends a sample update). Force an engine with `CONTAINER_ENGINE=podman ./run.sh`.
> Manual steps below — swap `docker compose` for `podman compose` under Podman.

```bash
# 1. Start the broker  (Podman: `podman compose up -d`)
cd docker && docker compose up -d && cd ..

# 2. Start the consumer (it declares the stream idempotently and waits)
dotnet run --project notification-service/NotificationService

# 3. In another terminal, start the publisher API
dotnet run --project customer-service/CustomerService

# 4. Trigger an update (changes email -> consumer reacts; changing only name -> consumer ignores)
curl -X PUT http://localhost:5080/customers/C-001 \
  -H 'content-type: application/json' \
  -d '{"fullName":"Ada Lovelace","email":"ada.new@example.com","phoneNumber":"+1-555-0100"}'
```
