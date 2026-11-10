using Microsoft.Extensions.Options;

namespace AiControlCenter.Worker.Service;

public sealed class IntegrationsExecutionOptions
{
    public const string SectionName = "IntegrationsGrpc";
    public string Address { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public int DeadlineSeconds { get; init; } = 90;
}
public sealed class IntegrationsExecutionOptionsValidator : IValidateOptions<IntegrationsExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, IntegrationsExecutionOptions options)
    {
        if (!Uri.TryCreate(options.Address, UriKind.Absolute, out _)) return ValidateOptionsResult.Fail("IntegrationsGrpc:Address must be absolute.");
        if (string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey.Length < 32) return ValidateOptionsResult.Fail("IntegrationsGrpc:ApiKey must contain at least 32 characters.");
        return options.DeadlineSeconds is >= 5 and <= 300 ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("IntegrationsGrpc:DeadlineSeconds must be between 5 and 300.");
    }
}
