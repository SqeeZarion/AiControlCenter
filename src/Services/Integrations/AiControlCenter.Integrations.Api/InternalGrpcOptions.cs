using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Api;

public sealed class InternalGrpcOptions
{
    public const string SectionName = "InternalGrpc";
    public string ApiKey { get; init; } = string.Empty;
}
public sealed class InternalGrpcOptionsValidator : IValidateOptions<InternalGrpcOptions>
{
    public ValidateOptionsResult Validate(string? name, InternalGrpcOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApiKey) && options.ApiKey.Length >= 32
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("InternalGrpc:ApiKey must contain at least 32 characters.");
}
