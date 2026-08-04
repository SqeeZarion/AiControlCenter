# Identity security operations

## Development setup

Generate an ignored 3072-bit RSA pair without external packages:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\security\New-IdentityDevelopmentKeys.ps1
```

Copy `.env.example` to the ignored `.env` and set
`IDENTITY_BOOTSTRAP_EMAIL` and `IDENTITY_BOOTSTRAP_TEMPORARY_PASSWORD`. The
temporary password must contain 12–128 characters. Values and key material
must never be committed or logged.

The Identity API owns the private key. Gateway, ControlPlane, Orchestrator and
Integrations receive only a read-only mount of the public key.

## Manual signing-key rotation

Stage 2 deliberately has no partial discovery/JWKS implementation. Rotation
is a coordinated operation:

1. Generate a new pair in a protected location.
2. Change `Authentication:KeyId` and `JwtIssuer:KeyId` to a new unique value.
3. Stop traffic that creates new sessions.
4. Replace the public-key mounts on all validators and the private-key mount
   on Identity.
5. Restart validators and Identity together.
6. Verify login and protected API/SignalR/gRPC calls.
7. Retire the old private key securely.

This creates a controlled restart window. Zero-downtime multi-key rotation is
deferred until a complete OIDC/JWKS solution is adopted.

## Bootstrap and secret files

Bootstrap is idempotent and runs only when no active Admin exists. Production
should set `IdentityBootstrap:EmailFile` and
`IdentityBootstrap:TemporaryPasswordFile` to files supplied by the deployment
secret store. Direct configuration values are intended only for the ignored
Development `.env`.

## Data Protection

Docker Development persists ASP.NET Core Data Protection keys in the
`identity_data_protection` named volume. Production must use durable storage
shared by all Identity replicas and protect those keys at rest using the
platform key-management facility. Data Protection files are never source
artifacts.

The application process remains the non-root image user (`1654:1654`). A
one-shot Compose initializer owns the narrow privilege boundary: before
migrations and Identity start, it assigns the named volume to that user,
restricts the directory to mode `700`, and existing key files to mode `600`.
This makes both fresh and reused Docker Desktop/Linux volumes writable without
running the Identity application as root or using world-writable permissions.

## Session behavior

- Access lifetime: at most 10 minutes; clock skew: at most 30 seconds.
- Refresh family absolute lifetime: at most 30 days.
- Refresh, logout and cookie-backed session mutation require both a trusted
  Origin and antiforgery token.
- Five failed password attempts cause a 15-minute account lockout.
- Angular uses a same-tab single-flight promise and the browser Web Locks API
  to serialize refresh across tabs. Browsers without Web Locks retain
  same-tab single-flight behavior; a genuine cross-tab race is treated as
  refresh-token reuse and invalidates the family.
