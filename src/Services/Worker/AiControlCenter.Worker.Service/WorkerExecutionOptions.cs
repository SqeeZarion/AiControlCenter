using Microsoft.Extensions.Options;

namespace AiControlCenter.Worker.Service;

public sealed class WorkerExecutionOptions
{
    public const string SectionName = "OrchestratorGrpc";
    public string Address { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public int DeadlineSeconds { get; init; } = 5;
}

public sealed class WorkerExecutionOptionsValidator : IValidateOptions<WorkerExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerExecutionOptions options)
    {
        if (!Uri.TryCreate(options.Address, UriKind.Absolute, out _))
            return ValidateOptionsResult.Fail("OrchestratorGrpc:Address must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey.Length < 32)
            return ValidateOptionsResult.Fail("OrchestratorGrpc:ApiKey must contain at least 32 characters.");
        return options.DeadlineSeconds is >= 1 and <= 30
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("OrchestratorGrpc:DeadlineSeconds must be between 1 and 30.");
    }
}
