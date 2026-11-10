using System.Security.Cryptography;
using System.Text;
using AiControlCenter.Integrations.Application;
using Microsoft.AspNetCore.DataProtection;

namespace AiControlCenter.Integrations.Infrastructure.Security;

internal sealed class ProviderCredentialProtector(IDataProtectionProvider provider) : IProviderCredentialProtector
{
    private const string RootPurpose = "AiControlCenter.Integrations.Secrets.v2";
    public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) =>
        CredentialProtector(context).Protect(credential);
    public string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context) =>
        CredentialProtector(context).Unprotect(protectedCredential);
    public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) =>
        OAuthProtector(context).Protect(verifier);
    public string UnprotectOAuthVerifier(string protectedVerifier, OAuthVerifierProtectionContext context) =>
        OAuthProtector(context).Unprotect(protectedVerifier);
    public string Fingerprint(string credential)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        return $"sha256:{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
    private IDataProtector CredentialProtector(ProviderCredentialProtectionContext context) =>
        provider.CreateProtector(RootPurpose, "provider-credential", "v1",
            context.Provider.ToString(), context.OwnerUserId.ToString("N"),
            context.ConnectionId.ToString("N"), context.CredentialVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private IDataProtector OAuthProtector(OAuthVerifierProtectionContext context) =>
        provider.CreateProtector(RootPurpose, "oauth-pkce-verifier", "v1",
            context.Provider.ToString(), context.OwnerUserId.ToString("N"), context.FlowId.ToString("N"));
}
