# Keycloak + Redis Integration PoC

See [SPEC.md](SPEC.md) for scope. This README is minimal for now and will be completed in US-8.

## Start / stop

```sh
docker compose up -d     # Keycloak on localhost:8080, Redis on localhost:6379
docker compose down
```

Keycloak takes ~20–30 s to become ready. Admin console: http://localhost:8080/admin — login `admin` / `admin`.

Check Redis: `docker compose exec redis redis-cli ping` → `PONG`.

The `lab` realm is re-imported from `keycloak/import/lab-realm.json` on every start, so `down`/`up` never loses it. Changes made in the admin console are **not** kept — put them in that file instead.

## Realm contents

| Thing        | Value                                          |
|--------------|------------------------------------------------|
| Realm        | `lab`                                          |
| Client       | `lab-api` (public, direct access grants on; tokens carry `aud: lab-api`) |
| Realm role   | `admin`                                        |
| Test user    | `testuser` / `testpass`, has role `admin`      |

## Get an access token

```sh
curl -s -X POST http://localhost:8080/realms/lab/protocol/openid-connect/token -d grant_type=password -d client_id=lab-api -d username=testuser -d password=testpass
```

On Windows PowerShell, type `curl.exe` instead of `curl`. In Windows PowerShell 5.1, plain `curl` runs a different command (`Invoke-WebRequest`) that doesn't accept these options.

The `access_token` field of the response is the JWT; paste it into https://jwt.io to inspect it. Tokens expire after 5 minutes.

This uses the OAuth *password grant*, which is deprecated in OAuth 2.1 — it's used here only because it gets a token with a single command and no browser.
