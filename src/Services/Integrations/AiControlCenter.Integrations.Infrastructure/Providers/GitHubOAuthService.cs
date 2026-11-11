using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

public enum GitHubOAuthConfigurationStatus { Available, NotConfigured }
public sealed record GitHubOAuthStatusResult(GitHubOAuthConfigurationStatus Status);
public sealed record GitHubOAuthStartResult(
    GitHubOAuthConfigurationStatus Status,
    Uri? AuthorizationUri);
public sealed record GitHubOAuthCompleteResult(Guid OwnerUserId, ProviderConnectionDto Connection);
public sealed class GitHubOAuthNotConfiguredException()
    : InvalidOperationException("GitHub OAuth is not configured.");

public sealed class GitHubOAuthService(IProviderOAuthStateRepository states, IIntegrationsUnitOfWork unitOfWork,
    IProviderCredentialProtector protector, ProviderConnectionService connections, IHttpClientFactory clients,
    IOptions<GitHubOAuthOptions> configuredOptions, TimeProvider timeProvider)
{
    private readonly GitHubOAuthOptions options = configuredOptions.Value;

    public GitHubOAuthStatusResult GetStatus() => new(options.Enabled
        ? GitHubOAuthConfigurationStatus.Available
        : GitHubOAuthConfigurationStatus.NotConfigured);

    public async Task<GitHubOAuthStartResult> StartAsync(
        Guid owner,
        string? requestedRedirectUri,
        string? returnUrl,
        CancellationToken token)
    {
        if (!options.Enabled)
            return new(GitHubOAuthConfigurationStatus.NotConfigured, null);
        if (!string.IsNullOrWhiteSpace(returnUrl))
            throw new ArgumentException("A client-provided OAuth return URL is not allowed.", nameof(returnUrl));
        if (!string.IsNullOrWhiteSpace(requestedRedirectUri)
            && !string.Equals(requestedRedirectUri, options.CallbackUrl, StringComparison.Ordinal))
            throw new ArgumentException("The OAuth redirect URI does not match the configured callback.", nameof(requestedRedirectUri));

        var redirectUri = options.CallbackUrl;
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var flowId = Guid.NewGuid();
        states.Add(new ProviderOAuthState(flowId, owner, Hash(state), protector.ProtectOAuthVerifier(verifier,
            new OAuthVerifierProtectionContext(AiProviderKind.GitHubCopilot, owner, flowId)), redirectUri,
            timeProvider.GetUtcNow().AddMinutes(10)));
        await unitOfWork.SaveChangesAsync(token);
        var uri = new Uri("https://github.com/login/oauth/authorize?" + string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "read:user",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        }.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}")));
        return new(GitHubOAuthConfigurationStatus.Available, uri);
    }

    public async Task<GitHubOAuthCompleteResult> CompleteAsync(string state, string code, CancellationToken token)
    {
        if (!options.Enabled)
            throw new GitHubOAuthNotConfiguredException();
        var entity = await states.GetByHashAsync(Hash(state), token) ?? throw new InvalidOperationException("OAuth state is invalid.");
        if (!entity.TryConsume(timeProvider.GetUtcNow())) throw new InvalidOperationException("OAuth state expired or was already used.");
        await unitOfWork.SaveChangesAsync(token);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = JsonContent.Create(new
            {
                client_id = options.ClientId,
                client_secret = options.ClientSecret,
                code,
                redirect_uri = entity.RedirectUri,
                code_verifier = protector.UnprotectOAuthVerifier(entity.CodeVerifierProtected,
                    new OAuthVerifierProtectionContext(AiProviderKind.GitHubCopilot, entity.OwnerUserId, entity.Id)),
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await clients.CreateClient(nameof(GitHubOAuthService)).SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<GitHubTokenResponse>(cancellationToken: token);
        if (string.IsNullOrWhiteSpace(payload?.AccessToken)) throw new InvalidOperationException("GitHub did not return an access token.");
        DateTimeOffset? expiresAt = payload.ExpiresIn is > 0
            ? timeProvider.GetUtcNow().AddSeconds(payload.ExpiresIn.Value) : null;
        DateTimeOffset? refreshExpiresAt = payload.RefreshTokenExpiresIn is > 0
            ? timeProvider.GetUtcNow().AddSeconds(payload.RefreshTokenExpiresIn.Value) : null;
        var credential = GitHubOAuthCredential.Serialize(
            payload.AccessToken, payload.RefreshToken, expiresAt, refreshExpiresAt);
        var connection = await connections.CreateOrReplaceAsync(entity.OwnerUserId, AiProviderKind.GitHubCopilot,
            "GitHub Copilot", credential, token);
        return new(entity.OwnerUserId, connection);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record GitHubTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("refresh_token_expires_in")] int? RefreshTokenExpiresIn);
}
