using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class ProviderSecurityTests
{
    [Fact]
    public void ProtectedSecretsAreBoundToTheirExactPurposeContextAndStableKeyRing()
    {
        var directory = Directory.CreateTempSubdirectory("aicontrolcenter-purpose-test-");
        try
        {
            var ownerA = Guid.NewGuid();
            var ownerB = Guid.NewGuid();
            var connectionA = Guid.NewGuid();
            var connectionB = Guid.NewGuid();
            var provider1 = DataProtectionProvider.Create(directory,
                options => options.SetApplicationName("AiControlCenter.Integrations.Tests"));
            var protector1 = new ProviderCredentialProtector(provider1);
            var credentialContext = new ProviderCredentialProtectionContext(
                AiProviderKind.OpenAi, ownerA, connectionA, 1);
            var ciphertext = protector1.ProtectCredential("credential", credentialContext);

            var providerAfterRestart = DataProtectionProvider.Create(directory,
                options => options.SetApplicationName("AiControlCenter.Integrations.Tests"));
            var protector2 = new ProviderCredentialProtector(providerAfterRestart);
            Assert.Equal("credential", protector2.UnprotectCredential(ciphertext, credentialContext));
            Assert.ThrowsAny<Exception>(() => protector2.UnprotectCredential(ciphertext,
                credentialContext with { Provider = AiProviderKind.XAi }));
            Assert.ThrowsAny<Exception>(() => protector2.UnprotectCredential(ciphertext,
                credentialContext with { OwnerUserId = ownerB }));
            Assert.ThrowsAny<Exception>(() => protector2.UnprotectCredential(ciphertext,
                credentialContext with { ConnectionId = connectionB }));
            Assert.ThrowsAny<Exception>(() => protector2.UnprotectOAuthVerifier(ciphertext,
                new OAuthVerifierProtectionContext(AiProviderKind.OpenAi, ownerA, connectionA)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApiKeyConnectionRequiresProtectedCredential()
    {
        Assert.Throws<ArgumentException>(() => ProviderConnection.Create(Guid.NewGuid(), AiProviderKind.OpenAi,
            "OpenAI", null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UnknownProviderKindIsRejectedBeforePersistence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderConnection.Create(
            Guid.NewGuid(), (AiProviderKind)99, "Unknown", "ciphertext", "fingerprint", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RevocationRemovesStoredCredential()
    {
        var item = ProviderConnection.Create(Guid.NewGuid(), AiProviderKind.XAi, "xAI", "ciphertext", "fingerprint", DateTimeOffset.UtcNow);
        item.Revoke(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Null(item.ProtectedCredential);
        Assert.Equal(ProviderConnectionStatus.Revoked, item.Status);
    }

    [Theory]
    [InlineData(ProviderFailureKind.QuotaExceeded, true)]
    [InlineData(ProviderFailureKind.RateLimited, true)]
    [InlineData(ProviderFailureKind.TemporarilyUnavailable, true)]
    [InlineData(ProviderFailureKind.ModelUnavailable, true)]
    [InlineData(ProviderFailureKind.InsufficientFunds, true)]
    [InlineData(ProviderFailureKind.Authentication, false)]
    [InlineData(ProviderFailureKind.Validation, false)]
    [InlineData(ProviderFailureKind.Safety, false)]
    [InlineData(ProviderFailureKind.Cancelled, false)]
    [InlineData(ProviderFailureKind.Tool, false)]
    [InlineData(ProviderFailureKind.Configuration, false)]
    [InlineData(ProviderFailureKind.LocalBudgetExceeded, false)]
    [InlineData(ProviderFailureKind.AmbiguousTimeout, false)]
    public void FallbackPolicyIsStrict(ProviderFailureKind kind, bool expected) =>
        Assert.Equal(expected, ProviderFallbackPolicy.CanFallback(kind));

    [Fact]
    public void OAuthStateIsSingleUseAndExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new ProviderOAuthState(Guid.NewGuid(), Guid.NewGuid(), "hash", "verifier", "https://localhost/callback", now.AddMinutes(1));
        Assert.True(state.TryConsume(now));
        Assert.False(state.TryConsume(now.AddSeconds(1)));
        var expired = new ProviderOAuthState(Guid.NewGuid(), Guid.NewGuid(), "hash2", "verifier", "https://localhost/callback", now);
        Assert.False(expired.TryConsume(now));
    }
}
