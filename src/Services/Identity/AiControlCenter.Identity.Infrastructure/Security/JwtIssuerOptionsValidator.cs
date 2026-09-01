using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Identity.Infrastructure.Security;

//Він перевіряє всі JWT-налаштування під час запуску Identity. Якщо щось неправильне — сервіс не запускається.
internal sealed class JwtIssuerOptionsValidator : IValidateOptions<JwtIssuerOptions>
{
    private readonly bool validateKeyMaterial;

    public JwtIssuerOptionsValidator()
        : this(validateKeyMaterial: true)
    {
    }

    internal JwtIssuerOptionsValidator(bool validateKeyMaterial)
    {
        this.validateKeyMaterial = validateKeyMaterial;
    }

    public ValidateOptionsResult Validate(string? name, JwtIssuerOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer)) failures.Add("Authentication:Issuer is required.");
        if (string.IsNullOrWhiteSpace(options.Audience)) failures.Add("Authentication:Audience is required.");
        if (string.IsNullOrWhiteSpace(options.KeyId)) failures.Add("Authentication:KeyId is required.");
        if (string.IsNullOrWhiteSpace(options.PublicKeyPath)) failures.Add("Authentication:PublicKeyPath is required.");
        if (string.IsNullOrWhiteSpace(options.PrivateKeyPath)) failures.Add("JwtSigning:PrivateKeyPath is required.");
        if (!string.Equals(options.Algorithm, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
        {
            failures.Add("Authentication:Algorithm must be RS256.");
        }

        if (options.AccessTokenMinutes is < 1 or > 10)
        {
            failures.Add("JwtSigning:AccessTokenMinutes must be between 1 and 10.");
        }

        if (!string.IsNullOrWhiteSpace(options.LegacyIssuer)
            && !string.Equals(options.LegacyIssuer, options.Issuer, StringComparison.Ordinal))
        {
            failures.Add("Legacy JwtIssuer:Issuer conflicts with canonical Authentication:Issuer.");
        }

        if (!string.IsNullOrWhiteSpace(options.LegacyAudience)
            && !string.Equals(options.LegacyAudience, options.Audience, StringComparison.Ordinal))
        {
            failures.Add("Legacy JwtIssuer:Audience conflicts with canonical Authentication:Audience.");
        }

        if (failures.Count == 0 && validateKeyMaterial)
        {
            if (!IdentityRsaKeySnapshot.TryCreate(options, out var snapshot, out var keyFailure))
            {
                failures.Add(keyFailure);
            }
            else
            {
                snapshot!.Dispose();
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
