# ADR 0001: Closed first-party session protocol

- Status: Accepted
- Date: 2026-08-03
- Scope: Stage 2 Identity and authorization

## Context

AiControlCenter currently has one trusted Angular client served through the
same frontend Nginx and Gateway. It has no third-party clients, federation or
external delegated authorization requirements.

## Decision

Identity implements a closed first-party session protocol, not an OAuth or
OpenID Connect authorization server.

- Angular submits credentials only to the versioned Identity login endpoint.
- Identity returns an RSA-signed access JWT with a maximum lifetime of ten
  minutes.
- The access token is held only in Angular memory.
- Identity sends a rotating opaque refresh token in an HttpOnly,
  SameSite=Strict cookie and stores only its SHA-256 hash.
- Refresh reuse revokes the entire token family.
- Gateway and every internal API validate issuer, audience, RSA signature,
  algorithm, expiration and token-use claim.
- Public registration is not supported. Only Admins create users.

## Consequences and limitations

This protocol is intentionally limited to the single first-party browser
client. It must not be advertised as OAuth/OIDC, used for third-party consent,
or extended with ad-hoc grants. Logout, blocking and role changes can leave an
already-issued access token valid until its short expiration.

If external, mobile or independently operated clients are introduced, the
protocol must be replaced with OpenIddict or another reviewed OIDC provider
using Authorization Code with PKCE. The custom password/token endpoints must
not become a general authorization server.
