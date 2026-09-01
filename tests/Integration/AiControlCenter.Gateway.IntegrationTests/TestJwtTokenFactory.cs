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
        PrivatePkcs1KeyPath = Path.Combine(DirectoryPath, "private-pkcs1.pem");
        PrivatePkcs8KeyPath = Path.Combine(DirectoryPath, "private-pkcs8.pem");
        File.WriteAllText(PublicKeyPath, Rsa.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(PrivatePkcs1KeyPath, Rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(PrivatePkcs8KeyPath, Rsa.ExportPkcs8PrivateKeyPem());
    }

    public static string PublicKeyPath { get; }

    public static string PrivatePkcs1KeyPath { get; }

    public static string PrivatePkcs8KeyPath { get; }

    public static string Issue(
        string role = "Admin",
        bool passwordChangeRequired = false,
        string keyId = "identity-dev-2026-01",
        string issuer = "AiControlCenter.Identity",
        string audience = "aicontrolcenter-api",
        string algorithm = SecurityAlgorithms.RsaSha256,
        string? tokenUse = "access")
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("jti", Guid.NewGuid().ToString()),
            new("pwd_change_required", passwordChangeRequired ? "true" : "false"),
            new("role", role),
        };
        if (tokenUse is not null)
        {
            claims.Add(new Claim("token_use", tokenUse));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddMinutes(10).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(Rsa) { KeyId = keyId },
                algorithm),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
