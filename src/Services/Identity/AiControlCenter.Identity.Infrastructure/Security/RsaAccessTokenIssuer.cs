using System.Security.Claims;
using System.Security.Cryptography;
using System.Globalization;
using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Identity.Infrastructure.Security;

internal sealed class RsaAccessTokenIssuer : IAccessTokenIssuer, IDisposable
{
    private readonly JwtIssuerOptions options;
    private readonly RSA rsa;
    private readonly SigningCredentials signingCredentials;
    private readonly JsonWebTokenHandler tokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

    public RsaAccessTokenIssuer(IOptions<JwtIssuerOptions> options)
    {
        this.options = options.Value;
        Validate(this.options);
        rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(this.options.PrivateKeyPath));
        signingCredentials = new SigningCredentials(
            new RsaSecurityKey(rsa) { KeyId = this.options.KeyId },
            SecurityAlgorithms.RsaSha256);
    }

    public AccessTokenResult Issue(
        User user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset issuedAt)
    {
        var expiresAt = issuedAt.AddMinutes(options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("token_use", "access"),
            new("pwd_change_required", user.MustChangePassword ? "true" : "false", ClaimValueTypes.Boolean),
        };
        claims.AddRange(roles.Select(role => new Claim("role", role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = signingCredentials,
        };

        return new AccessTokenResult(tokenHandler.CreateToken(descriptor), expiresAt);
    }

    public void Dispose() => rsa.Dispose();

    private static void Validate(JwtIssuerOptions options)
    {
        if (options.AccessTokenMinutes is < 1 or > 10)
        {
            throw new InvalidOperationException("JWT access token lifetime must be between 1 and 10 minutes.");
        }

        if (string.IsNullOrWhiteSpace(options.Issuer)
            || string.IsNullOrWhiteSpace(options.Audience)
            || string.IsNullOrWhiteSpace(options.KeyId)
            || string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            throw new InvalidOperationException("JWT issuer configuration is incomplete.");
        }

        if (!File.Exists(options.PrivateKeyPath))
        {
            throw new FileNotFoundException("The JWT private key file was not found.", options.PrivateKeyPath);
        }
    }
}
