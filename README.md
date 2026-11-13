# Core.Gateway

An API gateway built on ASP.NET Core 10 and [YARP](https://github.com/dotnet/yarp). The solution contains the gateway, three sample APIs for local development, and a unit and integration test suite.

Core.Gateway is intended to become the data plane of **GateFlow**, an API gateway with a management console. See [docs/ROADMAP.md](docs/ROADMAP.md) for the plan.

## Features

- **Authentication**: JWT from one or more issuers, hashed API keys, and RSA request signatures with replay protection.
- **Routing and headers**: per-route policies, removal of identity headers sent by clients, claim-to-header mapping, and CORS.
- **Rate limiting**: per-pod limits per cluster and per IP, shared sliding-window quotas in Redis per user, tenant, API key, and route, and a local fallback while Redis is unavailable.
- **Resilience**: request timeouts, bounded retries for idempotent requests, and passive (optionally active) backend health checks.
- **Edge security**: host filtering, trusted forwarded headers, request size limits, security response headers, and a basic injection filter.
- **Observability**: correlation IDs, structured logging, OpenTelemetry traces and metrics, and liveness and readiness endpoints.

## Repository layout

| Path | Contents |
|---|---|
| `src/Core.Gateway.Api/` | The gateway, organised by concern: authentication, security, rate limiting, Redis, resilience, observability |
| `services/` | Development-only sample APIs for users, orders, and products |
| `tests/Core.Gateway.Api.Tests/` | Unit and integration tests |
| `tests/fixtures/examples/` | Sample configurations, each checked by a test |
| `tests/postman/` | Postman collection for the gateway endpoints |

## Running locally

Requires the .NET 10 SDK. Start each API in its own terminal:

```powershell
dotnet run --project services/User.Api --launch-profile http      # http://localhost:5001
dotnet run --project services/Order.Api --launch-profile http     # http://localhost:5002
dotnet run --project services/Product.Api --launch-profile http   # http://localhost:5003
dotnet run --project src/Core.Gateway.Api --launch-profile http   # http://localhost:8080
```

Authentication is disabled in the Development environment so you can call the sample APIs directly. The gateway exposes `/api/users`, `/api/orders`, and `/api/products`, plus short aliases (`/users`, `/orders`, `/products`).

## Checks

These are the same steps CI runs:

```powershell
dotnet restore Core.Gateway.sln
dotnet format Core.Gateway.sln --verify-no-changes --no-restore
dotnet build Core.Gateway.sln -c Release --no-restore
dotnet test Core.Gateway.sln -c Release --no-build --no-restore
pwsh -File tests/smoke-development.ps1
```

Tests that need a real Redis are skipped unless `GATEWAY_TEST_REDIS` points to a running instance (for example `127.0.0.1:6379`). `tests/test-redis.ps1` starts a temporary Redis and runs the whole suite against it.

## Configuration

Defaults live in `src/Core.Gateway.Api/appsettings*.json`, and routes and clusters live in `src/Core.Gateway.Api/Configuration/reverseproxy*.json`. Environment variables override both, for example `RateLimiting__Distributed__Enabled=true`. Never commit real credentials to configuration files or test fixtures.

Outside Development, the gateway refuses to start unless authentication is enabled, `GatewaySecurity:AllowedHosts` is restricted, and trusted proxies are set.

| Section | Purpose |
|---|---|
| `Authentication` | JWT providers, API key clients (SHA-256 hashes only), and signature clients |
| `Headers:ClaimsToHeaders` | Claims forwarded to backends as headers |
| `Cors` | Allowed origins, methods, and headers |
| `GatewaySecurity` | Allowed hosts, trusted proxies and networks, and maximum request body size |
| `Redis` | Connection string, key prefix, timeouts, and failure cooldown |
| `RateLimiting` | Local limits, shared quotas, local fallback, and named route policies |
| `Resilience` | Timeouts, retries, and backend health checks |
| `HealthChecks` | Liveness and readiness paths, and an optional dedicated management port |
| `Observability` | Service name, OTLP exporter, and trace sampling |
| `Compression` | Response compression over HTTPS |

### Options worth knowing

| Key | Default | Description |
|---|---|---|
| `Compression:EnableForHttps` | `false` | Compress HTTPS responses. Leave this off when a response can contain secrets alongside user-controlled data (BREACH). |
| `Observability:TraceSamplingRatio` | `1.0` | Fraction of new traces to record, from 0 to 1. A parent span's decision always wins. Lower it under heavy traffic. |
| `HealthChecks:ManagementPort` | not set | When set, health endpoints are served only on this port and proxied routes only on the other ports. |

### Route metadata

Set these values under `Metadata` on a route in `reverseproxy.json`:

| Key | Values | Description |
|---|---|---|
| `RateLimitPolicy` | policy name | Applies a policy defined under `RateLimiting:Policies`. |
| `RetryAttempts` | `0`–`3` | Overrides `Resilience:RetryAttempts` for the route. |
| `MaxRequestBodySize` | bytes | Overrides the global request body limit. |
| `Streaming` | `true` / `false` | Disables the request timeout for long-lived responses. |
| `InjectionFilter` | `true` / `false` | `false` turns off the XSS and SQL signatures, for routes that accept free-text search. Null-byte and path traversal checks always run. |

The gateway validates route metadata when it loads the configuration, and rejects invalid values at startup or reload.

### Rate limiting order

Requests are checked in this order:

1. **Per-IP limit before authentication.** Requests that carry no credential are counted against `RateLimiting:Local:Anonymous` before they reach the per-cluster limit, so a single IP cannot use up a cluster's capacity.
2. **Per-cluster and per-pod limits.**
3. **Authentication.**
4. **Per-IP limit after authentication.** Requests whose credentials were rejected are counted here.
5. **Route policies.**
6. **Shared quotas in Redis.**
