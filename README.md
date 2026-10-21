# Keycloak + Redis Integration PoC

Proof of concept: an ASP.NET Core API secured by **Keycloak** (local JWT validation, role-based authorization), with a slow endpoint cached in **Redis**. Verified scenarios include forged and expired tokens, 401 vs 403, cache TTL, and Redis or Keycloak being unavailable. See [SPEC.md](SPEC.md) for the user stories.

## How it works

**Keycloak handles login.** It holds the users, passwords and roles. A user logs in to Keycloak, not to the API, and gets back an **access token**: a signed JWT that says who they are (`preferred_username`) and which roles they have (`realm_access.roles`). The token expires after 5 minutes.

**The API checks tokens. It never sees passwords.** When the API starts, it downloads Keycloak's public signing key once. After that, it checks each token's signature, issuer (`lab` realm), audience (`lab-api`) and expiry **locally**, without calling Keycloak on each request. A token that has been edited, has expired, or comes from somewhere else is rejected.

- **401 Unauthorized** means "I don't know who you are": no token, or an invalid one.
- **403 Forbidden** means "I know who you are, but you're not allowed": a valid token without the required role.

Keycloak says *who* the user is and *what roles* they have. The API decides *what each role may do*.

**Redis is a cache.** `/api/products` takes about 2 seconds, simulating a slow database query. The first call saves the result in Redis under `products:all` for 60 seconds, and calls within that minute are answered from Redis in milliseconds. If Redis is down, the API still works: it skips the cache and does the slow load. Redis has nothing to do with login.

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/), running
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `curl` (built into Windows 10+). On Windows PowerShell, type **`curl.exe`**, not `curl`: in PowerShell 5.1, plain `curl` runs `Invoke-WebRequest`, which doesn't accept these options.

## Start up

1. Start Keycloak (port 8080) and Redis (port 6379):
   ```sh
   docker compose up -d
   ```
   Keycloak needs about 20–30 s before it's ready. The admin console is at http://localhost:8080/admin, login `admin` / `admin`.

2. Start the API on http://localhost:5050. Leave it running in its own terminal and stop it with Ctrl+C.
   ```sh
   dotnet run --project src/LabApi
   ```

3. Check Redis is alive. It should print `PONG`:
   ```sh
   docker compose exec redis redis-cli ping
   ```

To stop everything: Ctrl+C in the API terminal, then `docker compose down`.

The `lab` realm is re-imported from [keycloak/import/lab-realm.json](keycloak/import/lab-realm.json) on every start, so `down`/`up` never loses it. Changes made in the admin console are **not** kept. Put them in that file instead.

## Realm contents

| Thing      | Value                                                                 |
|------------|-----------------------------------------------------------------------|
| Realm      | `lab`                                                                 |
| Client     | `lab-api` (public, direct access grants on; tokens carry `aud: lab-api`) |
| Realm role | `admin`                                                               |
| User       | `testuser` / `testpass`, has role `admin`                             |
| User       | `plainuser` / `plainpass`, no `admin` role (gets 403 on `/api/admin`) |

## Get a token

```sh
curl -s -X POST http://localhost:8080/realms/lab/protocol/openid-connect/token -d grant_type=password -d client_id=lab-api -d username=testuser -d password=testpass
```

The `access_token` field of the response is the JWT. Paste it into https://jwt.io to see what's inside. For `plainuser`, change the username and password.

This uses the OAuth *password grant*. It's deprecated in OAuth 2.1 and used here only because it gets a token in one command, without a browser.

The commands below need the token in a variable. In **PowerShell**:

```powershell
$token = (Invoke-RestMethod -Method Post -Uri http://localhost:8080/realms/lab/protocol/openid-connect/token -Body @{grant_type='password'; client_id='lab-api'; username='testuser'; password='testpass'}).access_token
```

In **bash**:

```sh
token=$(curl -s -X POST http://localhost:8080/realms/lab/protocol/openid-connect/token -d grant_type=password -d client_id=lab-api -d username=testuser -d password=testpass | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
```

## Endpoints

| Endpoint             | No token | `plainuser` | `testuser` (admin) |
|----------------------|----------|-------------|--------------------|
| `GET /api/public`    | 200      | 200         | 200                |
| `GET /api/protected` | 401      | 200         | 200                |
| `GET /api/admin`     | 401      | **403**     | 200                |
| `GET /api/products`  | 200      | 200         | 200                |

`-i` prints the status line (`HTTP/1.1 200 OK`, `401 Unauthorized`, ...).

**Public**: no token needed:
```sh
curl -i http://localhost:5050/api/public
```

**Protected**: any valid token. It returns the username and roles read from the token:
```sh
curl -i -H "Authorization: Bearer $token" http://localhost:5050/api/protected
```

**Admin**: needs the `admin` role. Try it with a `plainuser` token to see 403:
```sh
curl -i -H "Authorization: Bearer $token" http://localhost:5050/api/admin
```

**Products**: slow the first time, then cached for 60 s. Watch `cached` and the time:
```sh
curl -s -w "\ntime: %{time_total}s\n" http://localhost:5050/api/products
```

Inspect the cache:
```sh
docker compose exec redis redis-cli GET products:all
docker compose exec redis redis-cli TTL products:all
```
`TTL` is the number of seconds left. `-2` means the key has expired and is gone.

## Test scenarios

- **Edit a token** on jwt.io, for example change `preferred_username`, then send it. The result is 401, because the signature no longer matches.
- **Stop Keycloak** (`docker compose stop keycloak`) and call `/api/protected` with a valid token. It still returns 200, because the API checks tokens itself. Bring Keycloak back with `docker compose start keycloak`.
- **Stop Redis** (`docker compose stop redis`) and call `/api/products`. The data is still correct, just slow, with `cached: false`.
- **Wait 5 minutes** and reuse a token. You get 401, because it has expired. The API allows no grace period (`ClockSkew = 0`).
