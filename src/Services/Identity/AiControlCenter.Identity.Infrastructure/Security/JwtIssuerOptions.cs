namespace AiControlCenter.Identity.Infrastructure.Security;

//Цей клас зберігає налаштування створення JWT access-токенів.
public sealed class JwtIssuerOptions
{
    public string Issuer { get; set; } = string.Empty;

    //для кого токен призначений
    public string Audience { get; set; } = string.Empty;

    public string PublicKeyPath { get; set; } = string.Empty;

    public string PrivateKeyPath { get; set; } = string.Empty;

    //ідентифікатор ключа, яким підписали JWT.
    public string KeyId { get; set; } = string.Empty;

    public string Algorithm { get; set; } = "RS256";

    //Це строк дії access token у хвилинах.
    public int AccessTokenMinutes { get; set; } = 10;

    // не дозволяють приймати старі JWT. Вони потрібні для перевірки старої конфігурації, щоб вона не суперечила новій.
    public string? LegacyIssuer { get; set; }

    public string? LegacyAudience { get; set; }
}

public sealed class JwtSigningOptions
{
    public const string SectionName = "JwtSigning";

    public string PrivateKeyPath { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 10;
}
