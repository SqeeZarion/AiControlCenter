using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

public sealed class GitHubOAuthOptions
{
    public const string SectionName = "Providers:GitHub";
    public const string CallbackPath = "/api/integrations/v1/provider-connections/github/oauth/callback";

    public bool Enabled { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string CallbackUrl { get; init; } = string.Empty;
}

public sealed class GitHubOAuthOptionsValidator : IValidateOptions<GitHubOAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubOAuthOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ClientId))
            failures.Add($"{GitHubOAuthOptions.SectionName}:ClientId is required when GitHub OAuth is enabled.");
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            failures.Add($"{GitHubOAuthOptions.SectionName}:ClientSecret is required when GitHub OAuth is enabled.");
        if (!TryValidateCallback(options.CallbackUrl, out var callbackFailure))
            failures.Add(callbackFailure);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static bool TryValidateCallback(string? value, out string failure)
    {
        failure = $"{GitHubOAuthOptions.SectionName}:CallbackUrl must be an absolute allowlisted OAuth callback URL.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var callback)
            || !string.IsNullOrEmpty(callback.UserInfo)
            || !string.IsNullOrEmpty(callback.Query)
            || !string.IsNullOrEmpty(callback.Fragment)
            || !callback.AbsolutePath.Equals(GitHubOAuthOptions.CallbackPath, StringComparison.Ordinal)
            || (callback.Scheme != Uri.UriSchemeHttps
                && !(callback.IsLoopback && callback.Scheme == Uri.UriSchemeHttp)))
            return false;

        failure = string.Empty;
        return true;
    }
}
