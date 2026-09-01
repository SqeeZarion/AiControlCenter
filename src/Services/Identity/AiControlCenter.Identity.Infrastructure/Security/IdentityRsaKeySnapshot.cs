using System.Security.Cryptography;
using System.Text;

namespace AiControlCenter.Identity.Infrastructure.Security;

//завантажувач і сховище RSA-ключів Identity у пам’яті.
public sealed class IdentityRsaKeySnapshot : IDisposable
{
    private const int MaximumPemSizeBytes = 64 * 1024;
    private int disposed;

    private IdentityRsaKeySnapshot(RSA publicKey, RSA privateKey)
    {
        PublicKey = publicKey;
        PrivateKey = privateKey;
    }

    public RSA PublicKey { get; }

    internal RSA PrivateKey { get; }

    internal static bool TryCreate(
        JwtIssuerOptions options,
        out IdentityRsaKeySnapshot? snapshot,
        out string failure)
    {
        snapshot = null;
        RSA? publicKey = null;
        RSA? privateKey = null;
        var readingPrivateKey = false;
        try
        {
            if (!TryReadBoundedPem(
                    options.PublicKeyPath,
                    "Authentication:PublicKeyPath",
                    out var publicPem,
                    out failure))
            {
                return false;
            }

            if (ContainsPrivatePemLabel(publicPem))
            {
                failure =
                    "Authentication:PublicKeyPath contains private key material; a public-only RSA PEM key is required.";
                return false;
            }

            publicKey = RSA.Create();
            publicKey.ImportFromPem(publicPem);
            if (HasPrivateParameters(publicKey))
            {
                failure =
                    "Authentication:PublicKeyPath contains private key material; a public-only RSA PEM key is required.";
                return false;
            }

            if (publicKey.KeySize < 2048)
            {
                failure = "The JWT RSA public key must be at least 2048 bits.";
                return false;
            }

            readingPrivateKey = true;
            if (!TryReadBoundedPem(
                    options.PrivateKeyPath,
                    "JwtSigning:PrivateKeyPath",
                    out var privatePem,
                    out failure))
            {
                return false;
            }

            privateKey = RSA.Create();
            privateKey.ImportFromPem(privatePem);
            if (!HasPrivateParameters(privateKey) || privateKey.KeySize < 2048)
            {
                failure = "JWT signing keys could not be loaded as a valid RSA key pair.";
                return false;
            }

            var probe = RandomNumberGenerator.GetBytes(32);
            var signature = privateKey.SignData(
                probe,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            if (!publicKey.VerifyData(
                    probe,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                failure = "JWT private and public keys do not form a matching pair.";
                return false;
            }

            snapshot = new IdentityRsaKeySnapshot(publicKey, privateKey);
            publicKey = null;
            privateKey = null;
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            failure = readingPrivateKey
                ? "JWT signing keys could not be loaded as a valid RSA key pair."
                : "Authentication:PublicKeyPath is not a valid RSA public PEM key.";
            return false;
        }
        finally
        {
            publicKey?.Dispose();
            privateKey?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        PrivateKey.Dispose();
        PublicKey.Dispose();
    }

    private static bool TryReadBoundedPem(
        string path,
        string configurationKey,
        out string pem,
        out string failure)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumPemSizeBytes)
            {
                pem = string.Empty;
                failure = $"{configurationKey} exceeds the maximum supported PEM size of 65536 bytes.";
                return false;
            }

            var bytes = new byte[MaximumPemSizeBytes + 1];
            var totalRead = 0;
            while (totalRead < bytes.Length)
            {
                var read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead > MaximumPemSizeBytes)
            {
                pem = string.Empty;
                failure = $"{configurationKey} exceeds the maximum supported PEM size of 65536 bytes.";
                return false;
            }

            var contentOffset = totalRead >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF
                    ? 3
                    : 0;
            pem = Encoding.UTF8.GetString(bytes, contentOffset, totalRead - contentOffset);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            pem = string.Empty;
            failure = $"{configurationKey} file was not found.";
            return false;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            pem = string.Empty;
            failure = $"{configurationKey} could not be read.";
            return false;
        }
    }

    private static bool ContainsPrivatePemLabel(string pem) =>
        pem.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal)
        || pem.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
        || pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);

    private static bool HasPrivateParameters(RSA rsa)
    {
        try
        {
            var parameters = rsa.ExportParameters(includePrivateParameters: true);
            try
            {
                return parameters.D is { Length: > 0 }
                    || parameters.P is { Length: > 0 }
                    || parameters.Q is { Length: > 0 };
            }
            finally
            {
                Clear(parameters.D);
                Clear(parameters.DP);
                Clear(parameters.DQ);
                Clear(parameters.InverseQ);
                Clear(parameters.P);
                Clear(parameters.Q);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
