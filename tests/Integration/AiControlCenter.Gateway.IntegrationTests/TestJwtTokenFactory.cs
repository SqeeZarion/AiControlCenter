using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Gateway.IntegrationTests;

internal static class TestJwtTokenFactory
{
    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "aicontrolcenter-gateway-tests");

    static TestJwtTokenFactory()
    {
        Directory.CreateDirectory(DirectoryPath);
        PublicKeyPath = Path.Combine(DirectoryPath, "public.pem");
        File.WriteAllText(PublicKeyPath, Rsa.ExportSubjectPublicKeyInfoPem());
    }

    public static string PublicKeyPath { get; }

    public static string Issue(string role = "Admin", bool passwordChangeRequired = false)
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "AiControlCenter.Identity",
            Audience = "aicontrolcenter-api",
            Subject = new ClaimsIdentity([
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("jti", Guid.NewGuid().ToString()),
                new Claim("token_use", "access"),
                new Claim("pwd_change_required", passwordChangeRequired ? "true" : "false"),
                new Claim("role", role),
            ]),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddMinutes(10).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(Rsa) { KeyId = "identity-dev-2026-01" },
                SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
