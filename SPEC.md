# SPEC — Keycloak + Redis Integration PoC

Proof of concept evaluating **Keycloak 26** as the identity provider for an ASP.NET Core API (local JWT validation, role-based authorization) and **Redis 7** as a cache-aside layer, including behaviour when either service is unavailable.

## Goal

Verify end to end that an API can delegate authentication to Keycloak and validate its tokens locally, enforce role-based access, and cache a slow query in Redis without making Redis a hard dependency. Local environment only; not a production deployment.

## Out of scope

- Any frontend (no Angular, no UI)
- A real database — a hardcoded in-memory list is fine
- HTTPS, production hardening, deployment
- User registration, password reset, social login

## Stack

- .NET 10 Web API (minimal API or controllers — either)
- Keycloak 26, dev mode, in Docker
- Redis 7, in Docker
- Docker Compose for both services

---

## US-1 — Services run locally

As a developer, I want Keycloak and Redis running with one command, so the whole environment can be started and torn down reproducibly.

**Acceptance criteria**
- `docker compose up -d` starts Keycloak on `localhost:8080` and Redis on `localhost:6379`
- Keycloak admin console loads and accepts the bootstrap admin credentials
- `redis-cli ping` returns `PONG`
- `docker compose down` then `up -d` does not lose the Keycloak realm configuration (realm is persisted or re-importable)

---

## US-2 — Keycloak realm configured

As a developer, I want a realm with a client, a role, and a test user, so the API has something real to validate tokens against.

**Acceptance criteria**
- A realm named `lab` exists (not `master`)
- A client exists for the API, configured so tokens issued for it can be validated by the API
- A realm role `admin` exists
- A test user exists with a known password and the `admin` role assigned
- The realm configuration is exported to a file in the repo so it can be re-imported after a reset
- I can obtain an access token for the test user using a single `curl` command, and that command is documented in the README

---

## US-3 — Public endpoint

As a developer, I want one endpoint that requires no authentication, as a baseline that separates API failures from authentication failures.

**Acceptance criteria**
- `GET /api/public` returns 200 with a simple payload, with no `Authorization` header

---

## US-4 — Protected endpoint

As a developer, I want an endpoint that only accepts valid Keycloak tokens, to verify authentication is enforced.

**Acceptance criteria**
- `GET /api/protected` returns 401 when no token is sent
- Returns 401 when the token is expired or has an invalid signature
- Returns 200 when a valid token from the `lab` realm is sent as `Authorization: Bearer <token>`
- The 200 response includes the username and the roles read from the token, to verify which claims the token carries
- The API validates the token signature locally against Keycloak's published keys — it must NOT call Keycloak on every request

---

## US-5 — Role-protected endpoint

As a developer, I want an endpoint restricted to one role, to verify authorization (403) is enforced separately from authentication (401).

**Acceptance criteria**
- `GET /api/admin` returns 200 only for a user holding the `admin` role
- Returns 403 (not 401) for a validly authenticated user without that role
- Returns 401 with no token

---

## US-6 — Slow endpoint, uncached

As a developer, I want an endpoint that is deliberately slow, to give the cache a measurable baseline.

**Acceptance criteria**
- `GET /api/products` returns a list of products
- The handler contains an artificial delay of about 2 seconds to simulate a slow query
- The response includes a field indicating whether the data came from cache

---

## US-7 — Redis caching

As a developer, I want `/api/products` cached in Redis, to measure the latency gain and inspect the cached value.

**Acceptance criteria**
- First request takes roughly 2 seconds and reports `cached: false`
- Subsequent requests return in well under 100 ms and report `cached: true`
- The cached entry has a TTL of 60 seconds and disappears on its own afterwards
- The key is visible in `redis-cli` under a readable, namespaced name (e.g. `products:all`)
- If Redis is stopped, the API still returns correct data (slowly) instead of failing

---

## US-8 — README

As a developer, I want a README that gets the environment running from zero.

**Acceptance criteria**
- Prerequisites listed
- Start-up steps in order
- The `curl` command to get a token
- A `curl` command for each endpoint
- A short section explaining what Keycloak is doing and what Redis is doing here
