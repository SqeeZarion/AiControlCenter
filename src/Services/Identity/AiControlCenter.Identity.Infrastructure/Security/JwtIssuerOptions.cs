namespace AiControlCenter.Identity.Infrastructure.Security;

public sealed class JwtIssuerOptions
{
    public const string SectionName = "JwtIssuer";

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string PrivateKeyPath { get; init; } = string.Empty;

    public string KeyId { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 10;
}
