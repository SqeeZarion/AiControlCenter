using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Services.IntegrationTests;

internal static class TestJwtTokenFactory
{
    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "aicontrolcenter-service-tests");

    static TestJwtTokenFactory()
    {
        Directory.CreateDirectory(DirectoryPath);
        PublicKeyPath = Path.Combine(DirectoryPath, "public.pem");
        PrivateKeyPath = Path.Combine(DirectoryPath, "private.pem");
        File.WriteAllText(PublicKeyPath, Rsa.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(PrivateKeyPath, Rsa.ExportPkcs8PrivateKeyPem());
    }

    public static string PublicKeyPath { get; }

    public static string PrivateKeyPath { get; }

    public static string Issue(Guid? userId = null, string role = "Admin", bool passwordChangeRequired = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "AiControlCenter.Identity",
            Audience = "aicontrolcenter-api",
            Subject = new ClaimsIdentity([
                new Claim("sub", (userId ?? Guid.NewGuid()).ToString()),
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
        });
    }
}
