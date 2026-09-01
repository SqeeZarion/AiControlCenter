using System.Security.Cryptography;
using AiControlCenter.Security;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.BuildingBlocks.UnitTests;

public sealed class PlatformAuthenticationOptionsValidatorTests
{
    private const int MaximumPublicPemSizeBytes = 64 * 1024;

    [Fact]
    public async Task PublicOnlyRsaPemIsAccepted()
    {
        using var rsa = RSA.Create(2048);
        await WithTemporaryPemAsync(rsa.ExportSubjectPublicKeyInfoPem(), path =>
        {
            var result = Validate(path);

            Assert.Same(Microsoft.Extensions.Options.ValidateOptionsResult.Success, result);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(PrivatePemFormat.Pkcs1)]
    [InlineData(PrivatePemFormat.Pkcs8)]
    [InlineData(PrivatePemFormat.EncryptedPkcs8)]
    public async Task AnyPrivatePemFormatIsRejected(PrivatePemFormat format)
    {
        using var rsa = RSA.Create(2048);
        var pem = format switch
        {
            PrivatePemFormat.Pkcs1 => rsa.ExportRSAPrivateKeyPem(),
            PrivatePemFormat.Pkcs8 => rsa.ExportPkcs8PrivateKeyPem(),
            PrivatePemFormat.EncryptedPkcs8 => rsa.ExportEncryptedPkcs8PrivateKeyPem(
                "test-password",
                new PbeParameters(
                    PbeEncryptionAlgorithm.Aes256Cbc,
                    HashAlgorithmName.SHA256,
                    iterationCount: 10_000)),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        await WithTemporaryPemAsync(pem, path =>
        {
            var result = Validate(path);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("PublicKeyPath contains private key material", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MalformedPemProducesValidationFailure()
    {
        await WithTemporaryPemAsync("not-a-pem", path =>
        {
            var result = Validate(path);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("valid RSA public PEM key", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PublicPemAtMaximumSupportedSizeIsAccepted()
    {
        using var rsa = RSA.Create(2048);
        var pem = PadToSize(rsa.ExportSubjectPublicKeyInfoPem(), MaximumPublicPemSizeBytes);

        await WithTemporaryPemAsync(pem, path =>
        {
            Assert.Same(Microsoft.Extensions.Options.ValidateOptionsResult.Success, Validate(path));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OversizedPublicPemProducesBoundedValidationFailure()
    {
        using var rsa = RSA.Create(2048);
        var pem = PadToSize(rsa.ExportSubjectPublicKeyInfoPem(), MaximumPublicPemSizeBytes + 1);

        await WithTemporaryPemAsync(pem, path =>
        {
            var result = Validate(path);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("maximum supported PEM size", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MultiplePublicPemBlocksAreRejected()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var pem = $"{first.ExportSubjectPublicKeyInfoPem()}\n{second.ExportSubjectPublicKeyInfoPem()}";

        await WithTemporaryPemAsync(pem, path =>
        {
            var result = Validate(path);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("valid RSA public PEM key", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PublicAndPrivatePemBlocksAreRejectedAsPrivateMaterial()
    {
        using var rsa = RSA.Create(2048);
        var pem = $"{rsa.ExportSubjectPublicKeyInfoPem()}\n{rsa.ExportPkcs8PrivateKeyPem()}";

        await WithTemporaryPemAsync(pem, path =>
        {
            var result = Validate(path);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("contains private key material", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MalformedOversizedPemProducesBoundedValidationFailure()
    {
        await WithTemporaryPemAsync(new string('x', MaximumPublicPemSizeBytes + 1), path =>
        {
            var result = Validate(path);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("maximum supported PEM size", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    public enum PrivatePemFormat
    {
        Pkcs1,
        Pkcs8,
        EncryptedPkcs8,
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(string publicKeyPath) =>
        new PlatformAuthenticationOptionsValidator().Validate(null, new PlatformAuthenticationOptions
        {
            Issuer = "tests",
            Audience = "tests",
            PublicKeyPath = publicKeyPath,
            KeyId = "tests",
            Algorithm = SecurityAlgorithms.RsaSha256,
            ClockSkewSeconds = 0,
        });

    private static string PadToSize(string pem, int size)
    {
        Assert.True(pem.Length <= size);
        return pem + new string(' ', size - pem.Length);
    }

    private static async Task WithTemporaryPemAsync(string pem, Func<string, Task> assertion)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aicontrolcenter-public-key-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(path, pem);
        try
        {
            await assertion(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
