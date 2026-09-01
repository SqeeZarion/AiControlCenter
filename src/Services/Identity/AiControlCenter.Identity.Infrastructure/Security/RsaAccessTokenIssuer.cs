using System.Security.Claims;
using System.Globalization;
using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Identity.Infrastructure.Security;

//Створює короткочасний JWT access token і підписує його RSA-ключем
internal sealed class RsaAccessTokenIssuer : IAccessTokenIssuer
{
    private readonly JwtIssuerOptions options;
    private readonly SigningCredentials signingCredentials;
    private readonly JsonWebTokenHandler tokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

    public RsaAccessTokenIssuer(
        IOptions<JwtIssuerOptions> options,
        IdentityRsaKeySnapshot keySnapshot)
    {
        this.options = options.Value;
        //Створюється об’єкт для роботи з RSA.
        //Завантаження private key
        //Налаштування підпису
        var securityKey = new RsaSecurityKey(keySnapshot.PrivateKey)
        {
            KeyId = this.options.KeyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        signingCredentials = new SigningCredentials(securityKey, this.options.Algorithm);
    }

    //Метод створює access token для конкретного користувача.
    public AccessTokenResult Issue(
        User user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset issuedAt)
    {
        var expiresAt = issuedAt.AddMinutes(options.AccessTokenMinutes);
        //дані, записані всередину JWT.
        var claims = new List<Claim>
        {
            //користувач, якому належить токен.
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            //унікальний ID конкретного JWT.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            //час видачі JWT.
            new(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("token_use", "access"),
            new("pwd_change_required", user.MustChangePassword ? "true" : "false", ClaimValueTypes.Boolean),
        };
        //Для кожної ролі створюється окремий claim.
        claims.AddRange(roles.Select(role => new Claim("role", role)));
//-------------------------------------------------------------звідси починай------------------------------Для якої системи він призначений:----------------------
        //опис майбутнього токена
        var descriptor = new SecurityTokenDescriptor
        {
            //хто видав токен
            Issuer = options.Issuer,
            //Для якої системи він призначений
            Audience = options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = signingCredentials,
        };

        return new AccessTokenResult(tokenHandler.CreateToken(descriptor), expiresAt);
    }

}
