using AiControlCenter.Integrations.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

public sealed class LocalCopilotCliOptions
{
    public const string SectionName = "Providers:LocalCopilotCli";
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "local-copilot-cli:4321";
    public string ManagementEndpoint { get; init; } = "http://local-copilot-cli:4322";
    public string ConnectionToken { get; init; } = string.Empty;
    public Guid OwnerUserId { get; init; }
    public int RequestTimeoutSeconds { get; init; } = 120;
}

internal sealed class LocalCopilotCliOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<LocalCopilotCliOptions>
{
    private const string AllowedEndpoint = "local-copilot-cli:4321";
    private const string AllowedManagementEndpoint = "http://local-copilot-cli:4322";

    public ValidateOptionsResult Validate(string? name, LocalCopilotCliOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        var failures = new List<string>();
        if (!environment.IsDevelopment())
            failures.Add("Providers:LocalCopilotCli may only be enabled in Development.");
        if (!string.Equals(options.Endpoint, AllowedEndpoint, StringComparison.OrdinalIgnoreCase))
            failures.Add($"Providers:LocalCopilotCli:Endpoint must be the allowlisted internal endpoint '{AllowedEndpoint}'.");
        if (!string.Equals(options.ManagementEndpoint, AllowedManagementEndpoint, StringComparison.OrdinalIgnoreCase))
            failures.Add($"Providers:LocalCopilotCli:ManagementEndpoint must be the allowlisted internal endpoint '{AllowedManagementEndpoint}'.");
        if (options.ConnectionToken.Length is < 32 or > 256)
            failures.Add("Providers:LocalCopilotCli:ConnectionToken must contain 32-256 characters.");
        if (options.ConnectionToken.Any(char.IsWhiteSpace))
            failures.Add("Providers:LocalCopilotCli:ConnectionToken must not contain whitespace.");
        if (options.OwnerUserId == Guid.Empty)
            failures.Add("Providers:LocalCopilotCli:OwnerUserId is required.");
        if (options.RequestTimeoutSeconds is < 5 or > 300)
            failures.Add("Providers:LocalCopilotCli:RequestTimeoutSeconds must be between 5 and 300.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed record LocalCopilotCliStatusDto(
    bool Enabled,
    bool AvailableForCurrentUser,
    string RuntimeState,
    string SidecarState,
    string AuthenticationState);

public sealed class LocalCopilotCliAccessDeniedException()
    : Exception("Local Copilot CLI is not available for this user.");

public sealed class LocalCopilotCliAccess(
    IOptions<LocalCopilotCliOptions> options,
    ILocalCopilotRuntime runtime)
{
    private readonly LocalCopilotCliOptions value = options.Value;

    public bool IsAllowed(Guid ownerUserId) =>
        value.Enabled && value.OwnerUserId == ownerUserId;

    public async Task<LocalCopilotCliStatusDto> StatusAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var available = IsAllowed(ownerUserId);
        if (!available) return new(value.Enabled, false, "Disabled", "Unavailable", "Unknown");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(value.RequestTimeoutSeconds, 15)));
        try
        {
            var inspection = await runtime.InspectAsync(timeout.Token);
            return new(true, true,
                inspection.IsAuthenticated ? "Ready" : "AuthorizationRequired",
                "Ready",
                inspection.IsAuthenticated ? "Active" : "Unauthenticated");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(true, true, "Unavailable", "Unavailable", "Unknown");
        }
    }

    public void EnsureAllowed(Guid ownerUserId)
    {
        if (!IsAllowed(ownerUserId))
            throw new LocalCopilotCliAccessDeniedException();
    }
}
