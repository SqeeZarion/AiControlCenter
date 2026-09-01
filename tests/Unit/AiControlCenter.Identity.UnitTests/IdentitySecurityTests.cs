using System.Security.Cryptography;
using AiControlCenter.Identity.Domain;
using AiControlCenter.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Identity.UnitTests;

public sealed class IdentitySecurityTests
{
    private const int MaximumPublicPemSizeBytes = 64 * 1024;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IssuerValidatorRejectsPrivateSigningMaterialInPublicKeyPath(bool pkcs1)
    {
        var directory = Directory.CreateTempSubdirectory("aicontrolcenter-jwt-validator-");
        try
        {
            using var rsa = RSA.Create(2048);
            var publicPath = Path.Combine(directory.FullName, "public.pem");
            var privatePath = Path.Combine(directory.FullName, "private.pem");
            await File.WriteAllTextAsync(
                publicPath,
                pkcs1 ? rsa.ExportRSAPrivateKeyPem() : rsa.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(privatePath, rsa.ExportPkcs8PrivateKeyPem());
            var options = new JwtIssuerOptions
            {
                Issuer = "tests",
                Audience = "tests",
                PublicKeyPath = publicPath,
                PrivateKeyPath = privatePath,
                KeyId = "tests",
                Algorithm = SecurityAlgorithms.RsaSha256,
                AccessTokenMinutes = 10,
            };

            var result = new JwtIssuerOptionsValidator().Validate(null, options);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("PublicKeyPath contains private key material", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task IssuerValidatorRejectsOversizedPublicKeyWithBoundedValidationFailure()
    {
        var directory = Directory.CreateTempSubdirectory("aicontrolcenter-jwt-validator-size-");
        try
        {
            using var rsa = RSA.Create(2048);
            var publicPath = Path.Combine(directory.FullName, "public.pem");
            var privatePath = Path.Combine(directory.FullName, "private.pem");
            var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
            await File.WriteAllTextAsync(
                publicPath,
                publicPem + new string(' ', MaximumPublicPemSizeBytes + 1 - publicPem.Length));
            await File.WriteAllTextAsync(privatePath, rsa.ExportPkcs8PrivateKeyPem());

            var result = new JwtIssuerOptionsValidator().Validate(null, new JwtIssuerOptions
            {
                Issuer = "tests",
                Audience = "tests",
                PublicKeyPath = publicPath,
                PrivateKeyPath = privatePath,
                KeyId = "tests",
                Algorithm = SecurityAlgorithms.RsaSha256,
                AccessTokenMinutes = 10,
            });

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("maximum supported PEM size", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(true);
        }
    }

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
            var publicPath = Path.Combine(directory.FullName, "public.pem");
            using var rsa = RSA.Create(2048);
            await File.WriteAllTextAsync(privatePath, rsa.ExportRSAPrivateKeyPem());
            await File.WriteAllTextAsync(publicPath, rsa.ExportSubjectPublicKeyInfoPem());
            var issuerOptions = new JwtIssuerOptions
            {
                Issuer = "tests",
                Audience = "aicontrolcenter-api",
                PublicKeyPath = publicPath,
                PrivateKeyPath = privatePath,
                KeyId = "test-key",
                Algorithm = SecurityAlgorithms.RsaSha256,
                AccessTokenMinutes = 10,
            };
            Assert.True(
                IdentityRsaKeySnapshot.TryCreate(issuerOptions, out var snapshot, out var failure),
                failure);
            using var keySnapshot = snapshot!;
            var issuer = new RsaAccessTokenIssuer(Options.Create(issuerOptions), keySnapshot);
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

    [Fact]
    public async Task ConcurrentRsaIssuanceAndValidationPreservesKeyOwnershipAndCorrectness()
    {
        var directory = Directory.CreateTempSubdirectory("aicontrolcenter-jwt-concurrency-");
        try
        {
            var privatePath = Path.Combine(directory.FullName, "private.pem");
            var publicPath = Path.Combine(directory.FullName, "public.pem");
            using var signingRsa = RSA.Create(2048);
            await File.WriteAllTextAsync(privatePath, signingRsa.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(publicPath, signingRsa.ExportSubjectPublicKeyInfoPem());
            var issuerOptions = new JwtIssuerOptions
            {
                Issuer = "concurrency-tests",
                Audience = "aicontrolcenter-api",
                PublicKeyPath = publicPath,
                PrivateKeyPath = privatePath,
                KeyId = "concurrency-key",
                Algorithm = SecurityAlgorithms.RsaSha256,
                AccessTokenMinutes = 10,
            };
            Assert.True(
                IdentityRsaKeySnapshot.TryCreate(issuerOptions, out var snapshot, out var failure),
                failure);
            using var keySnapshot = snapshot!;
            var issuer = new RsaAccessTokenIssuer(Options.Create(issuerOptions), keySnapshot);
            using var validationRsa = RSA.Create();
            validationRsa.ImportFromPem(await File.ReadAllTextAsync(publicPath));
            var validationKey = new RsaSecurityKey(validationRsa)
            {
                KeyId = "concurrency-key",
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            };
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = validationKey,
                ValidateIssuer = true,
                ValidIssuer = "concurrency-tests",
                ValidateAudience = true,
                ValidAudience = "aicontrolcenter-api",
                ValidateLifetime = false,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            };
            var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
            var user = User.Create(
                Email.Create("concurrent@example.test"),
                DisplayName.Create("Concurrent User"),
                "hash",
                false,
                now);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = Enumerable.Range(0, 128).Select(async _ =>
            {
                await gate.Task;
                var issued = issuer.Issue(user, ["Admin"], now);
                var validation = await new JsonWebTokenHandler().ValidateTokenAsync(
                    issued.Token,
                    validationParameters);
                var jwt = new JsonWebToken(issued.Token);
                return (validation, jwt);
            }).ToArray();

            gate.SetResult();
            var results = await Task.WhenAll(operations);

            Assert.All(results, result =>
            {
                Assert.True(result.validation.IsValid, result.validation.Exception?.ToString());
                Assert.Equal("concurrency-key", result.jwt.Kid);
                Assert.Equal(SecurityAlgorithms.RsaSha256, result.jwt.Alg);
                Assert.Equal("concurrency-tests", result.jwt.Issuer);
                Assert.Contains("aicontrolcenter-api", result.jwt.Audiences);
            });
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
