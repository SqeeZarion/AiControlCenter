# Security scripts

Каталог містить локальні утиліти для підготовки криптографічних матеріалів Identity.

```mermaid
flowchart LR
    Generator["IdentityKeyGenerator"] --> Private["Private RSA key"]
    Generator --> Public["Public RSA key"]
    Private --> Identity["Identity"]
    Public --> Validators["Gateway і internal APIs"]
```

- [IdentityKeyGenerator](IdentityKeyGenerator/README.md)
- [Огляд Security](../../docs/security/security-overview.md)
