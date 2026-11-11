using System.Text.Json;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Providers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class GitHubOAuthConfigurationTests
{
    private const string Callback =
        "http://localhost:8080/api/integrations/v1/provider-connections/github/oauth/callback";
    private readonly GitHubOAuthOptionsValidator validator = new();

    [Fact]
    public void DisabledProviderDoesNotRequireCredentials()
    {
        var result = validator.Validate(null, new GitHubOAuthOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EnabledProviderRejectsMissingClientId()
    {
        var result = validator.Validate(null, new GitHubOAuthOptions
        {
            Enabled = true,
            ClientSecret = "client-secret",
            CallbackUrl = Callback,
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ClientId", StringComparison.Ordinal));
    }

    [Fact]
    public void EnabledProviderRejectsMissingClientSecret()
    {
        var result = validator.Validate(null, new GitHubOAuthOptions
        {
            Enabled = true,
            ClientId = "client-id",
            CallbackUrl = Callback,
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ClientSecret", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredProviderAcceptsOnlyTheExactCallbackPath()
    {
        Assert.True(validator.Validate(null, Configured()).Succeeded);
        Assert.True(validator.Validate(null, Configured(
            "http://localhost:8080/arbitrary/callback")).Failed);
        Assert.True(validator.Validate(null, Configured(
            Callback + "?returnUrl=https://attacker.example")).Failed);
    }

    [Fact]
    public async Task DisabledProviderReturnsTypedNotConfiguredWithoutPersistingState()
    {
        var repository = new OAuthStateRepository();
        var unitOfWork = new UnitOfWork();
        var service = CreateService(new GitHubOAuthOptions { Enabled = false }, repository, unitOfWork);

        var result = await service.StartAsync(Guid.NewGuid(), null, null, CancellationToken.None);

        Assert.Equal(GitHubOAuthConfigurationStatus.NotConfigured, result.Status);
        Assert.Null(result.AuthorizationUri);
        Assert.Null(repository.Added);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task ConfiguredProviderBuildsAuthorizationUrlWithServerCallbackAndNoSecret()
    {
        var repository = new OAuthStateRepository();
        var unitOfWork = new UnitOfWork();
        var options = Configured();
        var service = CreateService(options, repository, unitOfWork);

        var result = await service.StartAsync(Guid.NewGuid(), null, null, CancellationToken.None);
        var query = QueryHelpers.ParseQuery(result.AuthorizationUri!.Query);
        var serialized = JsonSerializer.Serialize(result);

        Assert.Equal(GitHubOAuthConfigurationStatus.Available, result.Status);
        Assert.Equal(options.ClientId, query["client_id"]);
        Assert.False(string.IsNullOrWhiteSpace(query["client_id"]));
        Assert.Equal(Callback, query["redirect_uri"]);
        Assert.Equal(Callback, repository.Added?.RedirectUri);
        Assert.DoesNotContain(options.ClientSecret, serialized, StringComparison.Ordinal);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Theory]
    [InlineData("http://localhost:8081/api/integrations/v1/provider-connections/github/oauth/callback", null)]
    [InlineData(null, "/providers")]
    [InlineData(null, "https://attacker.example/providers")]
    public async Task ClientCannotOverrideCallbackOrReturnUrl(string? redirectUri, string? returnUrl)
    {
        var service = CreateService(Configured(), new OAuthStateRepository(), new UnitOfWork());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.StartAsync(Guid.NewGuid(), redirectUri, returnUrl, CancellationToken.None));
    }

    private static GitHubOAuthService CreateService(
        GitHubOAuthOptions options,
        OAuthStateRepository repository,
        UnitOfWork unitOfWork) =>
        new(repository, unitOfWork, new CredentialProtector(), null!, null!,
            Options.Create(options), TimeProvider.System);

    private static GitHubOAuthOptions Configured(string callback = Callback) => new()
    {
        Enabled = true,
        ClientId = "client-id",
        ClientSecret = "client-secret-that-must-never-be-returned",
        CallbackUrl = callback,
    };

    private sealed class OAuthStateRepository : IProviderOAuthStateRepository
    {
        public ProviderOAuthState? Added { get; private set; }
        public Task<ProviderOAuthState?> GetByHashAsync(
            string stateHash,
            CancellationToken cancellationToken) => Task.FromResult<ProviderOAuthState?>(null);
        public void Add(ProviderOAuthState state) => Added = state;
    }

    private sealed class UnitOfWork : IIntegrationsUnitOfWork
    {
        public int SaveCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(++SaveCount);
    }

    private sealed class CredentialProtector : IProviderCredentialProtector
    {
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context) =>
            protectedCredential;
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string protectedVerifier, OAuthVerifierProtectionContext context) =>
            protectedVerifier;
        public string Fingerprint(string credential) => "fingerprint";
    }
}
