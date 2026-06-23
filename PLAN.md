# PLAN.md — RabbitMQ Streams playground

> Progress tracker. Each step has **Status** (`TODO` / `IN PROGRESS` / `DONE`) and
> **Result** (what actually happened). Claude: keep this current — see rules in
> [CLAUDE.md](CLAUDE.md). If a decision changes, update the decision table in
> CLAUDE.md and note it in the affected step's Result.

---

## Step 1 — Repo scaffolding, instructions & plan
- **Goal:** Create CLAUDE.md (instructions + decision log) and this PLAN.md.
- **Status:** DONE
- **Result:** Created `CLAUDE.md` (operating rules, topology, decision table, run
  guide) and `PLAN.md`. Detected env: .NET SDK 8.0.418 (+6/7), Docker 29.4.3,
  Compose v5.1.4. Chose net8.0 + RabbitMQ 4.x + `RabbitMQ.Stream.Client`.

## Step 2 — Containerized RabbitMQ with Streams enabled
- **Goal:** `docker/docker-compose.yml` + `enabled_plugins` + `rabbitmq.conf`
  exposing the stream protocol (:5552), management UI, retention-friendly config,
  app user, health check.
- **Status:** DONE
- **Result:** Added `docker/docker-compose.yml` (rabbitmq:4.1-management, stable
  hostname, ports 5552/5672/15672, named volume, healthcheck), `rabbitmq/enabled_plugins`
  (management + stream + stream_management), `rabbitmq/rabbitmq.conf`
  (`stream.advertised_host=localhost`, stream listener, log level). App user
  `app/app-pass` via env. Verified broker boots and 5552 is reachable (see Step 8).

## Step 3 — Customer Service (publisher) skeleton
- **Goal:** Minimal ASP.NET Core API: in-memory customers, `GET /customers`,
  `PUT /customers/{id}`. No messaging yet.
- **Status:** DONE
- **Result:** `dotnet new web` at `customer-service/`. In-memory `ConcurrentDictionary`
  store seeded with 3 customers, minimal-API endpoints, `Customer` model with
  per-record `Version`. Runs on http://localhost:5080.

## Step 4 — Notification Service (consumer) skeleton
- **Goal:** Worker service with an in-memory contact store. No messaging yet.
- **Status:** DONE
- **Result:** `dotnet new worker` at `notification-service/`. `ContactStore`
  (ConcurrentDictionary of last-known email/phone + version) added. Worker logs
  startup. No HTTP surface (pure consumer).

## Step 5 — Publisher integration (RabbitMQ Streams)
- **Goal:** Declare `customer.events` with retention; reliable `Producer` with
  publish confirms; publish `CustomerUpdated` snapshot on every update.
- **Status:** DONE
- **Result:** `StreamConnection` (owns `StreamSystem`, idempotent `CreateStream`
  with MaxAge/MaxLength/Segment), `CustomerEventPublisher` (reliable `Producer`,
  confirmation handler logging un-confirmed). `PUT` publishes the snapshot. Config
  in `appsettings.json` (`RabbitMq` section). Graceful shutdown disposes producer/system.

## Step 6 — Consumer integration (offset tracking + idempotency)
- **Goal:** Consume `customer.events`, resume from server-side stored offset,
  diff email/phone, update store on change, store offset, be idempotent on
  Version, single-active-consumer for safe horizontal scaling.
- **Status:** DONE
- **Result:** `StreamConsumerService` (BackgroundService): queries stored offset
  (`QueryOffset`) and resumes at `offset+1`, else `OffsetTypeFirst`. `Consumer`
  with `Reference="notification-service"`, `IsSingleActiveConsumer=true`. Handler
  deserializes, drops stale `Version`, diffs contact info, logs notification on
  email/phone change, then `StoreOffset`. Offset stored every N messages + on change.

## Step 7 — Build both solutions
- **Goal:** Both solutions restore + build clean on net8.0.
- **Status:** DONE
- **Result:** `dotnet build` green for both (see terminal). Pinned
  `RabbitMQ.Stream.Client` version recorded in csproj / CLAUDE.md decision table.

## Step 8 — End-to-end smoke test
- **Goal:** Start broker + both apps, update a customer, observe consumer reacting
  only on email/phone change and ignoring name-only changes; confirm offsets persist
  across consumer restart.
- **Status:** DONE
- **Result:** Ran broker + both apps live (2026-06-23).
  - Publisher logged 6 `Published CustomerUpdated` (C-001/2/3 v2 then v3); reliable
    producer, no un-confirmed sends.
  - Round 1 (first time each customer seen): consumer logged `Now tracking contact
    channels` ×3 (FirstSeen).
  - Round 2: C-001 email change → `emailChanged=True phoneChanged=False`; C-003
    phone change → `emailChanged=False phoneChanged=True`; **C-002 name-only change
    produced no notification** (NoContactChange, debug-suppressed). Exactly the
    intended behavior.
  - Restart test: stopped consumer (SIGINT), restarted → `Resuming from stored
    offset 10` and **reprocessed nothing** (no tracking/updated lines). Offset
    survived restart with NO external store.
  - **Gotcha learned:** the stream held 11 entries though we published only 6 user
    messages. RabbitMQ streams write **offset-tracking (and producer dedup) records
    inline into the log**, which advance the offset past the user-message count.
    This is by design and is precisely what makes server-side `QueryOffset`/resume
    work. Documented here so it isn't mistaken for double-publishing.
  - Healthcheck fix: initial `CMD` exec-form `&&` never ran (passed as a literal
    arg); switched to `CMD-SHELL`. Broker then reported healthy in ~3s.

---

## Backlog / nice-to-haves (not required)
- Shared `Contracts` NuGet package instead of duplicated record.
- `DeduplicatingProducer` with per-customer publishing-id for broker-side dedup.
- Super-stream (partitioned) for scale-out + `SuperStreamConsumer`.
- Dead-letter / poison-message handling on deserialize failure (currently logged & skipped).
- TLS on the stream listener (:5551) for real environments.
