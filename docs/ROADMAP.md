# GateFlow roadmap

GateFlow is an API gateway with a management console. From the console, operators manage routes, JWT providers, API keys, distributed rate limits, quotas, and circuit breakers, and see traffic analytics.

This document compares GateFlow with what Core.Gateway does today, proposes a target architecture, and breaks the work into phases.

## Starting point

Core.Gateway already provides most of the request-path behaviour GateFlow needs. What it lacks is a way to change that behaviour at runtime, and a way to see what it is doing.

| Capability | Core.Gateway today | Gap for GateFlow |
|---|---|---|
| Routes and clusters | YARP reads them from `reverseproxy.json`. File changes reload automatically, and config filters validate the metadata. | No API or UI, no version history, no rollback, and no record of who changed what. |
| JWT | Multiple issuers with authority discovery or static keys. Each provider is registered as an authentication scheme at startup. | Adding or changing a provider requires a restart. |
| API keys | SHA-256 hashes in configuration, loaded once into memory at startup. Each key carries scopes, roles, and a tenant. | No way to issue, rotate, expire, or revoke keys at runtime. No record of when a key was last used. |
| Distributed rate limiting | Sliding windows in Redis per user, tenant, API key, and route policy. A circuit breaker protects Redis calls, and a local fallback scales with the number of live pods. | Policies are fixed at startup. Limits cannot vary per consumer or per plan, and there is no way to see current usage. |
| Quotas | Only short windows, measured in seconds or minutes. | No daily or monthly quotas, no plans, and no usage history. |
| Traffic analytics | OpenTelemetry metrics (requests, authentication, rate limiting, proxy errors, retries, injection filter) exported to Prometheus. A Grafana dashboard covers traffic, 5xx ratio, p99 latency, backends per cluster, 429 sources, pods, and Redis quotas, and 12 Prometheus alerts are defined (commit `31b93a3`, currently not on `main`). | Metrics have no per-consumer breakdown, by design, to keep label cardinality low. The console cannot show who is calling what, or how much each consumer uses. |
| Circuit breaker | YARP passive health checks per destination (transport failure rate), optional active checks, and a 503 when every destination is unhealthy. | Backend 5xx responses and timeouts do not trip a breaker. There is no cluster-level open/half-open state, no view of breaker state, and no manual override. |

## Target architecture

```
                     ┌──────────────────────────── Control plane ───────────────────────────┐
  Operators ──OIDC──▶│  GateFlow.Admin.Web ──▶ GateFlow.Admin.Api ──▶ PostgreSQL            │
                     │  (React UI)             (config, consumers,    (source of truth,     │
                     │                          audit, analytics API)  audit log)           │
                     └──────────────────────────────┬───────────────────────────────────────┘
                                                    │ publish snapshot + notify
                                                    ▼
                                                  Redis ◀──── quotas, rate limits, key cache,
                                                    │          heartbeats, applied versions
                                                    ▼
  Clients ──────────────────────────────────▶ Core.Gateway pods (data plane) ──▶ Backends
                                                    │
                                                    │ OTLP metrics (exists)
                                                    ▼
                                         Prometheus + Grafana ◀── console reads
                                         (operational view)       the Prometheus API
```

### Design rules

1. **The control plane never sits on the request path.** Each gateway pod serves traffic from its last valid configuration snapshot. It keeps serving traffic when the Admin API, PostgreSQL, or Redis is down.
2. **One validator.** The Admin API and the gateway validate configuration with the same code, so a change that is accepted at publish time is never rejected by the gateway.
3. **Configuration is published as immutable versions.** Every publish creates a numbered snapshot. Rolling back means publishing an earlier snapshot again.
4. **Every write is audited.** The audit log records who made the change, when, and a before-and-after diff.
5. **Prometheus metrics use only low-cardinality labels.** Per-consumer data goes to the analytics store, never into metric labels.

### Configuration distribution

1. When an operator publishes, the Admin API validates the change. It then writes snapshot `N` to PostgreSQL, sets the Redis key `gateflow:config:current`, and sends a notification on a Redis channel.
2. Each gateway pod receives the notification and loads the snapshot. A pod also polls every 30 seconds in case it missed a notification. It validates the snapshot, swaps it in atomically through a custom `IProxyConfigProvider` and options sources, and writes it to local disk as its last known good configuration.
3. On startup, a pod loads the snapshot from Redis. If Redis is unavailable, it uses the copy on local disk. If neither exists, it uses the bundled file. This means the file-based configuration in use today keeps working.
4. Each pod reports the snapshot version it has applied through the existing Redis heartbeat. The console shows which pods are on which version.

## Feature design

### Routes and clusters
- Add CRUD in the Admin API for routes, clusters, destinations, transforms, and route metadata.
- Changes follow a draft → validate → publish → roll back workflow, and the console shows a diff before publishing.
- A dry-run endpoint runs the gateway's config filters against a draft.
- A route test tool shows which route and cluster a sample request would match.

### JWT providers
- Replace the per-provider authentication schemes with a single scheme. That scheme looks up validation parameters by issuer in the current snapshot, so providers can be added without a restart.
- Cache OIDC metadata per issuer with `ConfigurationManager<OpenIdConnectConfiguration>`. Keep today's behaviour of returning 503 when the identity provider cannot be reached.
- In the console, operators manage issuer, audience, algorithms, static keys or authority, and claim mappings.

### Consumers and API keys
- Add a **consumer** entity: an application or tenant that owns API keys and JWT subject mappings, and is assigned a plan.
- Key format: a public prefix used for display and lookup, followed by a random secret of at least 32 bytes. The full key is shown once, at creation. Only a SHA-256 hash is stored. A slow hash such as bcrypt is unnecessary for random secrets this long, and would add latency to every request.
- Support expiry, immediate revocation, and rotation in which the old and new keys both work for an overlap period.
- Gateway lookup goes through a local cache backed by Redis. A revocation removes the key from the cache through a Redis notification, with a target of under 5 seconds. Last-used timestamps are collected in memory and written to storage once a minute.

### Plans, rate limits, and quotas
- A **plan** defines rate limits (per second or minute) and quotas (per day or month). A consumer can override its plan's values.
- The existing shared quota middleware reads limits from the consumer's plan instead of global options.
- Quotas use calendar periods in UTC: Redis counters keyed by consumer and period, which expire at the end of the period plus a grace interval.
- When a quota is exceeded, the gateway returns 429 with the code `QUOTA_EXCEEDED` and quota headers. An alert fires when usage reaches 80% of the quota.
- Usage is rolled up hourly into PostgreSQL for history and billing. Operators can reset a quota or grant extra capacity from the console.

### Traffic analytics

Build on the existing collection pipeline instead of replacing it. Split the data by who needs it:

| View | Source | Work needed |
|---|---|---|
| Operational: RPS, 5xx, latency, backends, 429s, pods | Prometheus (exists) | Restore the dashboard and alerts. The console shows the key charts by querying the Prometheus HTTP API, and links to Grafana for detail. |
| Usage per consumer and API key: request counts, quota consumption | Redis counters and hourly rollups in PostgreSQL, from the quota work in phase 3 | Count requests per consumer, route, and status class alongside the quota counters. |
| Drill-down per request: latency and errors for one consumer, recent failed calls | Analytics store (optional) | Only if the two views above are not enough; see below. |

- Never add consumer or key IDs as Prometheus labels. Per-consumer data goes through the usage counters.
- If per-request drill-down becomes necessary, the gateway emits one event per response into a bounded in-memory queue and writes it in batches to ClickHouse. When the queue is full, events are dropped and a counter is incremented, so analytics never slows down a request. Raw events are kept for 7–14 days.

### Circuit breaker
- Each cluster gets a breaker with three states: closed, open, and half-open. It trips on the rate of 5xx responses, timeouts, and transport failures over a rolling window, once a minimum number of requests has been seen.
- While the breaker is open, the gateway returns 503 with `Retry-After` immediately, without calling the backend. In the half-open state, a small number of probe requests are let through to test whether the backend has recovered.
- Implement the breaker as a step in the YARP proxy pipeline, next to the existing retry step. Keep the destination-level passive health checks.
- Breaker state is kept per pod and reported through the heartbeat, so the console can show it for each pod. Settings and manual force-open or force-closed overrides are distributed in the configuration snapshot.

### Console and Admin API
- Admin API: ASP.NET Core minimal APIs, EF Core with PostgreSQL, and an OpenAPI description.
- Console: React, TypeScript, and Vite.
- Sign-in through OIDC (for example Keycloak or Entra ID), with three roles:
  - **Viewer**: read-only access.
  - **Operator**: can publish configuration and manage keys.
  - **Admin**: can also manage users and plans.
- The Admin API runs as its own deployment on an internal network. It is never exposed through the public gateway.

## Proposed solution layout

```
src/
  Core.Gateway.Api/             data plane (exists)
  GateFlow.Configuration/       snapshot model, options, and the validators shared by both planes
  GateFlow.Admin.Api/           control plane API
  GateFlow.Admin.Web/           console (React)
tests/
  Core.Gateway.Api.Tests/       (exists)
  GateFlow.Admin.Api.Tests/
  GateFlow.Contract.Tests/      checks that snapshots produced by the Admin API load in the gateway
```

## Phases

The phases are ordered by dependency. Every management feature needs dynamic configuration, so that comes first.

### Phase 0: Groundwork
- Move the options models and validators (rate limiting, JWT, CORS, route metadata) into `GateFlow.Configuration`.
- Define snapshot schema v1. Add a converter that turns today's `appsettings.json` and `reverseproxy.json` into a snapshot.
- Restore the deployment and observability assets from commit `31b93a3` (Dockerfile, Kubernetes manifests, Grafana dashboard, Prometheus rules) and the alert tests from `c3d42d9`.
- Record the decisions below as ADRs.

**Done when:** the gateway behaves identically whether it starts from a snapshot file or from today's configuration, and every existing test passes.

### Phase 1: Control plane MVP and dynamic configuration
- Admin API with PostgreSQL. Covers CRUD for routes, clusters, and rate limit policies, plus publish, roll back, and the audit log.
- Distribution through Redis, a dynamic configuration provider in the gateway, a last-known-good copy on disk, and the applied version reported for each pod.
- OIDC sign-in and role-based access.
- Console pages for routes, clusters, publish history, and pod status.

**Done when:**
- A route change published from the console is live on every pod within 5 seconds, without a restart.
- Pods keep serving traffic while the Admin API and Redis are down.
- An invalid snapshot is rejected before it is published.

### Phase 2: Consumers, API keys, and JWT providers
- Consumer entity, the full API key lifecycle, and the shared JWT scheme that picks validation settings by issuer.

**Done when:** a key revoked in the console stops working within 5 seconds, and a new issuer can be added without a restart.

### Phase 3: Plans and quotas
- Plans and per-consumer overrides, calendar quotas, quota headers and alerts, and usage history.

**Done when:** a monthly quota is enforced across all pods, and the console shows usage for each consumer.

### Phase 4: Traffic analytics in the console
- Console charts backed by the Prometheus HTTP API, with links to the existing Grafana dashboard.
- Usage pages per consumer and API key, built on the phase 3 counters.
- Add a ClickHouse event store only if per-request drill-down is needed.

**Done when:** the console shows the last 24 hours of traffic by route, and usage by consumer, without adding consumer IDs to Prometheus labels.

### Phase 5: Circuit breaker and live health
- Cluster breaker, breaker state in the heartbeat, a live health view, and manual overrides.

**Done when:** a failing backend trips the breaker and recovers through half-open probes, and the console shows the state for each pod.

### Phase 6: Hardening
- Load-test targets for throughput and p99 latency added by the gateway.
- Chaos tests with Redis, PostgreSQL, the Admin API, or a backend down.
- High availability for the Admin API, backups and restore, and a security review.
- Staged rollout of configuration: apply a snapshot to one pod first, and roll back automatically if readiness fails.

## Open decisions

| Decision | Options | Leaning |
|---|---|---|
| Console framework | React, Blazor | React: a larger ecosystem for dashboards and charts |
| Per-request analytics store | None, ClickHouse, TimescaleDB | None at first: Prometheus and the usage counters cover the console's needs |
| Configuration transport | Redis (snapshot and notifications), Admin API HTTP endpoint with ETag, Kubernetes ConfigMap | Redis, since the gateway already depends on it |
| Multi-tenancy | Single workspace, multiple workspaces with separate roles | Start with a single workspace, but keep a workspace ID in the schema |
| Deployment target | Kubernetes, VMs, managed containers | Kubernetes with Helm |

## Risks

- **A bad configuration reaches every pod at once.** Mitigations: shared validation, a dry-run endpoint, last-known-good fallback, and staged rollout in phase 6.
- **Redis becomes a single point of failure.** Mitigations: the existing local fallback for rate limits, cached configuration and keys, and Redis with replication.
- **Metric cardinality explodes.** Mitigation: per-consumer data stays out of Prometheus labels and goes through the usage counters.
- **Scope grows.** Each phase ends with a working product, and later phases can be reordered or dropped.
