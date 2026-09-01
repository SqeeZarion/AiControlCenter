# ADR 0004: JWT configuration і bounded revocation window

- Status: Accepted
- Date: 2026-08-23
- Scope: Access JWT issuance та validation

## Контекст

Незалежні issuer/audience settings для issuance і validation можуть створити
Identity, який виглядає healthy, але видає непридатні tokens. Stateless access
JWT водночас не можна негайно відкликати без per-request state lookup.

## Рішення

- `Authentication` є canonical validation configuration для Identity, Gateway
  та internal APIs: issuer, audience, key ID, RS256 і public-key path.
- Identity має лише додаткові signing settings: private-key path та access-token
  lifetime.
- Options проходять `ValidateOnStart`; Identity імпортує обидва keys і виконує
  cryptographic sign/verify probe до відкриття traffic.
- `Authentication:PublicKeyPath` є public-only boundary: private PKCS#1,
  private/encrypted PKCS#8 та RSA з доступними private parameters зупиняють
  startup. PEM content і private parameters не логуються.
- Підтримується тільки RS256. Mismatched, malformed або incomplete
  configuration зупиняє startup.
- Access JWT залишається stateless із цільовим lifetime 10 хвилин і не виконує
  database lookup на кожному request.
- Block, role change, password reset та logout негайно відкликають refresh
  sessions. Уже виданий access JWT може діяти до `exp` плюс configured clock
  skew; це прийняте bounded revocation window.
- `PasswordChanged` працює fail-closed і вимагає точний claim
  `pwd_change_required=false`.
- `PasswordChanged` вимагає рівно один такий claim; дублікати й суперечливі
  значення також відхиляються.

## Наслідки

Emergency access-token revocation і zero-downtime multi-key rotation не входять
до поточного first-party protocol. Для них потрібен token-version lookup,
deny-list або повноцінний OIDC/JWKS provider; вони не додаються приховано до
кожного API request.

[Identity security](../identity-security.md) · [Security overview](../../security/security-overview.md)
