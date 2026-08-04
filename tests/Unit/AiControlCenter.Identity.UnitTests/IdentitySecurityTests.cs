using System.Security.Cryptography;
using AiControlCenter.Identity.Domain;
using AiControlCenter.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Identity.UnitTests;

public sealed class IdentitySecurityTests
{
    [Fact]
    public void PasswordHashCanBeVerifiedAndWrongPasswordFails()
    {
        var hasher = new PasswordHasherAdapter();
        var hash = hasher.Hash("correct horse battery staple");

        Assert.NotEqual("correct horse battery staple", hash);
        Assert.NotEqual(Application.PasswordVerificationResult.Failed, hasher.Verify(hash, "correct horse battery staple"));
        Assert.Equal(Application.PasswordVerificationResult.Failed, hasher.Verify(hash, "wrong password"));
    }

    [Fact]
    public void RefreshGeneratorStoresSha256CompatibleHashInsteadOfRawToken()
    {
        var generator = new RefreshTokenGenerator();
        var token = generator.Generate();

        Assert.NotEqual(token.RawToken, token.Hash.Value);
        Assert.Equal(64, token.Hash.Value.Length);
        Assert.Equal(token.Hash, generator.Hash(token.RawToken));
    }

    [Fact]
    public async Task RsaIssuerCreatesTokenWithRequiredClaimsAndValidationBoundaries()
    {
        var directory = Directory.CreateTempSubdirectory("aicontrolcenter-jwt-");
        try
        {
            var privatePath = Path.Combine(directory.FullName, "private.pem");
            using var rsa = RSA.Create(2048);
            await File.WriteAllTextAsync(privatePath, rsa.ExportRSAPrivateKeyPem());
            var issuer = new RsaAccessTokenIssuer(Options.Create(new JwtIssuerOptions
            {
                Issuer = "tests",
                Audience = "aicontrolcenter-api",
                PrivateKeyPath = privatePath,
                KeyId = "test-key",
                AccessTokenMinutes = 10,
            }));
            var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
            var user = User.Create(
                Email.Create("admin@example.com"),
                DisplayName.Create("Admin User"),
                "hash",
                true,
                now);

            var token = issuer.Issue(user, ["Admin"], now);
            var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa) { KeyId = "test-key" },
                ValidateIssuer = true,
                ValidIssuer = "tests",
                ValidateAudience = true,
                ValidAudience = "aicontrolcenter-api",
                ValidateLifetime = false,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            });

            Assert.True(validation.IsValid);
            var jwt = new JsonWebToken(token.Token);
            Assert.Equal(user.Id.ToString(), jwt.Subject);
            Assert.Equal("access", jwt.GetClaim("token_use").Value);
            Assert.Equal("true", jwt.GetClaim("pwd_change_required").Value);
            Assert.Equal("test-key", jwt.Kid);
            Assert.DoesNotContain(jwt.Claims, claim => claim.Type == "email");
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
