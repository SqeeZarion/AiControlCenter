using System.Security.Cryptography;
using System.Text;

namespace AiControlCenter.Security;

internal static class RsaPublicKeyLoader
{
    public const int MaximumPemSizeBytes = 64 * 1024;
    public const string PrivateKeyFailure =
        "Authentication:PublicKeyPath contains private key material; a public-only RSA PEM key is required.";
    public const string OversizedKeyFailure =
        "Authentication:PublicKeyPath exceeds the maximum supported PEM size of 65536 bytes.";

    public static bool TryLoad(
        string path,
        out RSA? rsa,
        out string failure)
    {
        rsa = null;
        try
        {
            if (!TryReadBoundedPem(path, out var pem, out failure))
            {
                return false;
            }

            if (ContainsPrivatePemLabel(pem))
            {
                failure = PrivateKeyFailure;
                return false;
            }

            RSA? candidate = RSA.Create();
            try
            {
                candidate.ImportFromPem(pem);
                if (HasPrivateParameters(candidate))
                {
                    failure = PrivateKeyFailure;
                    return false;
                }

                if (candidate.KeySize < 2048)
                {
                    failure = "The JWT RSA public key must be at least 2048 bits.";
                    return false;
                }

                rsa = candidate;
                candidate = null;
                failure = string.Empty;
                return true;
            }
            finally
            {
                candidate?.Dispose();
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            failure = "Authentication:PublicKeyPath file was not found.";
            return false;
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            failure = "Authentication:PublicKeyPath is not a valid RSA public PEM key.";
            return false;
        }
    }

    private static bool TryReadBoundedPem(string path, out string pem, out string failure)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumPemSizeBytes)
        {
            pem = string.Empty;
            failure = OversizedKeyFailure;
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
            failure = OversizedKeyFailure;
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
                ClearPrivateParameters(parameters);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void ClearPrivateParameters(RSAParameters parameters)
    {
        Clear(parameters.D);
        Clear(parameters.DP);
        Clear(parameters.DQ);
        Clear(parameters.InverseQ);
        Clear(parameters.P);
        Clear(parameters.Q);
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
