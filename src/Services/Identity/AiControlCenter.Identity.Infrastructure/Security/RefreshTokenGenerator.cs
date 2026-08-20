using System.Security.Cryptography;
using System.Text;
using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Identity.Infrastructure.Security;

//Генерує довгий випадковий refresh token та його SHA-256 hash
internal sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    public GeneratedRefreshToken Generate()
    {
        var rawToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return new GeneratedRefreshToken(rawToken, Hash(rawToken));
    }

    public RefreshTokenHash Hash(string rawToken)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return RefreshTokenHash.Create(Convert.ToHexString(digest));
    }
}
