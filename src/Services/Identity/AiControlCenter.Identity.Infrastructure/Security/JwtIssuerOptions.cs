namespace AiControlCenter.Identity.Infrastructure.Security;

//Цей клас зберігає налаштування створення JWT access-токенів.
public sealed class JwtIssuerOptions
{
    public const string SectionName = "JwtIssuer";

    //хто видав токен
    public string Issuer { get; init; } = string.Empty;

    //для кого токен призначений
    public string Audience { get; init; } = string.Empty;

    public string PrivateKeyPath { get; init; } = string.Empty;

    //ідентифікатор ключа, яким підписали JWT.
    public string KeyId { get; init; } = string.Empty;

    //Це строк дії access token у хвилинах.
    public int AccessTokenMinutes { get; init; } = 10;
}
