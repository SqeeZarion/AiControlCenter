namespace AiControlCenter.Security;

//Це клас налаштувань JWT authentication.
public sealed class PlatformAuthenticationOptions
{
    public const string SectionName = "Authentication";
    //Сервіс приймає токен тільки від довіреного Identity.
    public string Issuer { get; init; } = string.Empty;
    //призначення токена
    public string Audience { get; init; } = string.Empty;
    //Шлях до публічного RSA-ключа:
    public string PublicKeyPath { get; init; } = string.Empty;

    public string KeyId { get; init; } = string.Empty;
    //Допустима різниця часу між серверами. (При ClockSkewSeconds = 30 токен ще може пройти перевірку. У коді дозволено максимум 30 секунд, щоб не продовжувати життя токена надто сильно.)
    public int ClockSkewSeconds { get; init; } = 30;
}
