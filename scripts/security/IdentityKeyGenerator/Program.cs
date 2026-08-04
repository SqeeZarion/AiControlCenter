using System.Security.Cryptography;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: IdentityKeyGenerator <output-directory> [--force]");
    return 2;
}

var outputDirectory = Path.GetFullPath(args[0]);
var force = args.Contains("--force", StringComparer.Ordinal);
var privateKeyPath = Path.Combine(outputDirectory, "identity-private.pem");
var publicKeyPath = Path.Combine(outputDirectory, "identity-public.pem");

if (!force && (File.Exists(privateKeyPath) || File.Exists(publicKeyPath)))
{
    Console.Error.WriteLine("Identity development keys already exist. Use --force only for intentional rotation.");
    return 1;
}

Directory.CreateDirectory(outputDirectory);
using var rsa = RSA.Create(3072);
await File.WriteAllTextAsync(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
await File.WriteAllTextAsync(publicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());

if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(privateKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    File.SetUnixFileMode(publicKeyPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
}

Console.WriteLine($"Generated ignored Development key pair in {outputDirectory}");
return 0;
