using Microsoft.Extensions.Options;

namespace AiControlCenter.Orchestrator.Api;

//Цей валідатор перевіряє під час запуску Orchestrator,
//чи налаштований достатньо довгий секретний API-ключ для захищеного gRPC-зв’язку з Worker.

public sealed class WorkerGrpcOptions
{
    public const string SectionName = "WorkerGrpc";
    public string ApiKey { get; init; } = string.Empty;
}

public sealed class WorkerGrpcOptionsValidator : IValidateOptions<WorkerGrpcOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerGrpcOptions options) =>
        string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey.Length < 32
            ? ValidateOptionsResult.Fail("WorkerGrpc:ApiKey must contain at least 32 characters.")
            : ValidateOptionsResult.Success;
}
