using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Security;

public sealed class PlatformAuthenticationOptionsValidator : IValidateOptions<PlatformAuthenticationOptions>
{
    private readonly RSA? preloadedPublicKey;

    public PlatformAuthenticationOptionsValidator()
    {
    }

    public PlatformAuthenticationOptionsValidator(RSA preloadedPublicKey)
    {
        this.preloadedPublicKey = preloadedPublicKey;
    }

    public ValidateOptionsResult Validate(string? name, PlatformAuthenticationOptions settings)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Issuer)) failures.Add("Authentication:Issuer is required.");
        if (string.IsNullOrWhiteSpace(settings.Audience)) failures.Add("Authentication:Audience is required.");
        if (string.IsNullOrWhiteSpace(settings.KeyId)) failures.Add("Authentication:KeyId is required.");
        if (string.IsNullOrWhiteSpace(settings.PublicKeyPath)) failures.Add("Authentication:PublicKeyPath is required.");
        if (!string.Equals(settings.Algorithm, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
        {
            failures.Add("Authentication:Algorithm must be RS256.");
        }

        if (settings.ClockSkewSeconds is < 0 or > 30)
        {
            failures.Add("Authentication:ClockSkewSeconds must be between 0 and 30.");
        }

        if (!string.IsNullOrWhiteSpace(settings.PublicKeyPath))
        {
            if (preloadedPublicKey is not null)
            {
                if (preloadedPublicKey.KeySize < 2048)
                {
                    failures.Add("The JWT RSA public key must be at least 2048 bits.");
                }
            }
            else if (!RsaPublicKeyLoader.TryLoad(settings.PublicKeyPath, out var rsa, out var failure))
            {
                failures.Add(failure);
            }
            else
            {
                rsa?.Dispose();
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
