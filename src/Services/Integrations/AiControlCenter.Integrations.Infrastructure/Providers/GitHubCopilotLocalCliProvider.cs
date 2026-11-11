using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using GitHub.Copilot;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

public interface ILocalCopilotRuntime
{
    Task<LocalCopilotRuntimeInspection> InspectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(CancellationToken cancellationToken);
    Task<LocalCopilotRuntimeResult> ExecuteAsync(
        string model,
        string systemPrompt,
        string input,
        IReadOnlyCollection<ToolDefinition> tools,
        IReadOnlyCollection<ToolInvocationResult> priorResults,
        CancellationToken cancellationToken);
}

public sealed record LocalCopilotRuntimeInspection(
    IReadOnlyCollection<ProviderModelDto> Models,
    string? GitHubLogin,
    bool IsAuthenticated);

public sealed record LocalCopilotRuntimeResult(
    string? Content,
    IReadOnlyCollection<ToolCallRequest> ToolCalls,
    string? ActualModel = null,
    string? ProviderRequestId = null,
    IReadOnlyCollection<ProviderOperationDiagnostic>? Diagnostics = null,
    string GenerationDispatched = "true");

internal interface ILocalCopilotSdkSession
{
    Task<AssistantMessageEvent?> SendAndWaitAsync(
        MessageOptions options,
        CancellationToken cancellationToken);
}

internal sealed class LocalCopilotSdkSession(CopilotSession session) : ILocalCopilotSdkSession
{
    public Task<AssistantMessageEvent?> SendAndWaitAsync(
        MessageOptions options,
        CancellationToken cancellationToken) =>
        session.SendAndWaitAsync(options, cancellationToken: cancellationToken);
}

internal sealed record LocalCopilotDispatchFailure(
    string ErrorCode,
    string Marker,
    string GenerationDispatched,
    string SafeExceptionType,
    string? ProviderRequestId = null,
    string? SafeMetadata = null);

internal sealed class LocalCopilotRuntimeException(
    string operation,
    string errorCode,
    string safeExceptionType,
    string generationDispatched,
    IReadOnlyCollection<ProviderOperationDiagnostic>? diagnostics = null,
    Exception? innerException = null)
    : Exception("Local Copilot runtime operation failed.", innerException)
{
    public string Operation { get; } = operation;
    public string ErrorCode { get; } = errorCode;
    public string SafeExceptionType { get; } = safeExceptionType;
    public string GenerationDispatched { get; } = generationDispatched;
    public IReadOnlyCollection<ProviderOperationDiagnostic> Diagnostics { get; } = diagnostics ?? [];

    public static LocalCopilotRuntimeException Predispatch(
        string operation,
        string errorCode,
        string safeExceptionType) =>
        new(operation, errorCode, safeExceptionType, "false");

    public static LocalCopilotRuntimeException Ambiguous(
        string operation,
        string errorCode,
        string safeExceptionType) =>
        new(operation, errorCode, safeExceptionType, "unknown");
}

internal sealed class GitHubCopilotLocalCliProvider(
    ILocalCopilotRuntime runtime,
    IOptions<LocalCopilotCliOptions> configuredOptions) : IAiProvider
{
    private readonly LocalCopilotCliOptions options = configuredOptions.Value;

    public AiProviderKind Kind => AiProviderKind.GitHubCopilotLocalCli;
    public bool IsAvailableForRouting => options.Enabled;

    public async Task<bool> ValidateAsync(string? credential, CancellationToken cancellationToken) =>
        (await ValidateConnectionAsync(credential, cancellationToken)).Kind
            == ProviderCredentialValidationKind.Valid;

    public async Task<ProviderCredentialValidationResult> ValidateConnectionAsync(
        string? credential,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new(ProviderCredentialValidationKind.TemporarilyUnavailable);
        try
        {
            var inspection = await runtime.InspectAsync(cancellationToken);
            if (!inspection.IsAuthenticated || string.IsNullOrWhiteSpace(inspection.GitHubLogin))
                return new(ProviderCredentialValidationKind.RequiresReconnect);
            if (inspection.Models.Count == 0)
                return new(ProviderCredentialValidationKind.ModelCatalogUnavailable,
                    ExternalAccountId: $"local-cli:{inspection.GitHubLogin}",
                    ExternalAccountLogin: inspection.GitHubLogin);
            return new(ProviderCredentialValidationKind.Valid, inspection.Models,
                $"local-cli:{inspection.GitHubLogin}", inspection.GitHubLogin,
                ProviderMetricSource.Unavailable, "Unavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(ProviderCredentialValidationKind.TemporarilyUnavailable);
        }
    }

    public async Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(
        string? credential,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var inspection = await runtime.InspectAsync(cancellationToken);
        if (!inspection.IsAuthenticated)
            throw new UnauthorizedAccessException("Local Copilot CLI authentication is unavailable.");
        if (inspection.Models.Count == 0)
            throw new InvalidOperationException("Local Copilot CLI model catalog is unavailable.");
        return inspection.Models;
    }

    public async Task<AiExecutionResult> ExecuteAsync(
        AiExecutionRequest request,
        string? credential,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            return Unavailable("local_copilot_cli_disabled");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await runtime.ExecuteAsync(
                request.ModelId,
                request.SystemPrompt,
                request.Input,
                request.ToolDefinitions ?? [],
                request.PriorToolResults ?? [],
                timeout.Token);
            if (response.ToolCalls.Count > 0)
                return new(false, null, ProviderFailureKind.None, null, response.ProviderRequestId, 0, 0, null, [],
                    response.ActualModel, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    null, ProviderMetricSource.Unavailable, ProviderMetricSource.Unavailable,
                    "ToolCalls", RequestedToolCalls: response.ToolCalls,
                    OutboundRequestCount: 1, Operation: "GenerationResponse",
                    GenerationDispatched: response.GenerationDispatched,
                    OperationDiagnostics: response.Diagnostics);
            if (string.IsNullOrWhiteSpace(response.Content))
                return new(false, null, ProviderFailureKind.Configuration,
                    "local_copilot_cli_protocol_invalid", response.ProviderRequestId, 0, 0, null, [],
                    OutboundRequestCount: 1, Operation: "GenerationResponse",
                    GenerationDispatched: response.GenerationDispatched,
                    OperationDiagnostics: response.Diagnostics);
            return new(true, response.Content, ProviderFailureKind.None, null, response.ProviderRequestId,
                0, 0, null, [], response.ActualModel,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                null, ProviderMetricSource.Unavailable, ProviderMetricSource.Unavailable,
                "Completed", OutboundRequestCount: 1, Operation: "GenerationResponse",
                GenerationDispatched: response.GenerationDispatched,
                OperationDiagnostics: response.Diagnostics);
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, null, ProviderFailureKind.Authentication,
                "connection_requires_reconnect", null, 0, 0, null, [],
                OutboundRequestCount: 0);
        }
        catch (LocalCopilotModelCatalogUnavailableException)
        {
            return new(false, null, ProviderFailureKind.ModelUnavailable,
                "model_catalog_unavailable", null, 0, 0, null, [],
                OutboundRequestCount: 0);
        }
        catch (LocalCopilotRuntimeException exception)
        {
            var failure = exception.GenerationDispatched == "unknown"
                ? ProviderFailureKind.AmbiguousTimeout
                : exception.ErrorCode switch
                {
                    "connection_requires_reconnect" or "generation_authentication_rejected" =>
                        ProviderFailureKind.Authentication,
                    "model_catalog_unavailable" or "model_not_available" =>
                        ProviderFailureKind.ModelUnavailable,
                    "generation_model_rejected" => ProviderFailureKind.ModelUnavailable,
                    "generation_rate_limited" => ProviderFailureKind.RateLimited,
                    "generation_policy_rejected" => ProviderFailureKind.Safety,
                    "runtime_start_unavailable" or "authentication_unavailable" =>
                        ProviderFailureKind.TemporarilyUnavailable,
                    _ => ProviderFailureKind.Configuration,
                };
            return new(false, null, failure, exception.ErrorCode, null, 0, 0, null, [],
                OutboundRequestCount: exception.GenerationDispatched == "false" ? 0 : 1,
                LatencyMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Operation: exception.Operation,
                GenerationDispatched: exception.GenerationDispatched,
                SafeExceptionType: exception.SafeExceptionType,
                OperationDiagnostics: exception.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, null, ProviderFailureKind.Cancelled, "cancelled",
                null, 0, 0, null, [], Operation: "None", GenerationDispatched: "false");
        }
        catch (OperationCanceledException)
        {
            return new(false, null, ProviderFailureKind.AmbiguousTimeout,
                "local_copilot_cli_outcome_unknown", null, 0, 0, null, [],
                OutboundRequestCount: 1, Operation: "Unknown",
                GenerationDispatched: "unknown",
                SafeExceptionType: nameof(OperationCanceledException));
        }
        catch (Exception exception)
        {
            return new(false, null, ProviderFailureKind.AmbiguousTimeout,
                "local_copilot_cli_unclassified_failure", null, 0, 0, null, [],
                OutboundRequestCount: 1, Operation: "Unknown",
                GenerationDispatched: "unknown",
                SafeExceptionType: exception.GetType().Name is nameof(HttpRequestException)
                    or nameof(InvalidOperationException) ? exception.GetType().Name : "ProviderRuntimeException");
        }
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled)
            throw new InvalidOperationException("Local Copilot CLI is disabled.");
    }

    private static AiExecutionResult Unavailable(string code) => new(
        false, null, ProviderFailureKind.TemporarilyUnavailable, code,
        null, 0, 0, null, [], OutboundRequestCount: 0,
        Operation: "None", GenerationDispatched: "false");
}

internal sealed partial class OfficialLocalCopilotRuntime(
    IOptions<LocalCopilotCliOptions> configuredOptions,
    ILogger<OfficialLocalCopilotRuntime> logger)
    : ILocalCopilotRuntime
{
    private readonly LocalCopilotCliOptions options = configuredOptions.Value;

    public async Task<LocalCopilotRuntimeInspection> InspectAsync(CancellationToken cancellationToken)
    {
        await using var client = CreateClient();
        await client.StartAsync(cancellationToken);
        var authStatus = await client.GetAuthStatusAsync(cancellationToken);
        var currentAuth = await client.Rpc.Account.GetCurrentAuthAsync(cancellationToken);
        var login = FindString(JsonSerializer.SerializeToElement(currentAuth), "login");
        var models = await ReadModelsAsync(client, cancellationToken);
        return new(models, login, authStatus.IsAuthenticated);
    }

    public async Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        await using var client = CreateClient();
        await client.StartAsync(cancellationToken);
        return await ReadModelsAsync(client, cancellationToken);
    }

    public async Task<LocalCopilotRuntimeResult> ExecuteAsync(
        string model,
        string systemPrompt,
        string input,
        IReadOnlyCollection<ToolDefinition> tools,
        IReadOnlyCollection<ToolInvocationResult> priorResults,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<ProviderOperationDiagnostic>();
        await using var client = CreateClient();
        await RunStageAsync("RuntimeStart", "false", diagnostics,
            async () => { await client.StartAsync(cancellationToken); return true; });
        var authStatus = await RunStageAsync("Authentication", "false", diagnostics,
            () => client.GetAuthStatusAsync(cancellationToken));
        if (!authStatus.IsAuthenticated)
        {
            AddDiagnostic(diagnostics, "Authentication", "Failed", 0,
                "connection_requires_reconnect", nameof(UnauthorizedAccessException),
                null, "false");
            LogStage("Authentication", "Failed", "connection_requires_reconnect",
                nameof(UnauthorizedAccessException), 0, "false", null);
            throw Failure("Authentication", "connection_requires_reconnect",
                new UnauthorizedAccessException(), "false", diagnostics);
        }
        var models = await RunStageAsync("ModelCatalog", "false", diagnostics,
            () => ReadModelsAsync(client, cancellationToken));
        if (models.Count == 0)
        {
            AddDiagnostic(diagnostics, "ModelCatalog", "Failed", 0,
                "model_catalog_unavailable", nameof(LocalCopilotModelCatalogUnavailableException),
                null, "false");
            LogStage("ModelCatalog", "Failed", "model_catalog_unavailable",
                nameof(LocalCopilotModelCatalogUnavailableException), 0, "false", null);
            throw Failure("ModelCatalog", "model_catalog_unavailable",
                new LocalCopilotModelCatalogUnavailableException(), "false", diagnostics);
        }
        LocalCopilotModelResolution resolution;
        try
        {
            resolution = ResolveModel(model, models, diagnostics);
            LogStage("ModelResolution", "Completed", "resolved", null, 0, "false", null);
        }
        catch (LocalCopilotRuntimeException exception)
        {
            LogStage(exception.Operation, "Failed", exception.ErrorCode,
                exception.SafeExceptionType, 0, exception.GenerationDispatched, null);
            throw;
        }
        var calls = new List<ToolCallRequest>();
        var sessionConfig = BuildSessionConfig(resolution.SessionModel, tools, calls);
        var sessionConfigurationFingerprint = SessionConfigurationFingerprint(
            sessionConfig, options.ConnectionToken);
        var session = await RunStageAsync("SessionCreate", "false", diagnostics,
            () => client.CreateSessionAsync(sessionConfig, cancellationToken),
            sessionConfigurationFingerprint);
        var priorContext = priorResults.Count == 0 ? string.Empty
            : "\n\nPreviously completed server tool results (do not repeat their call IDs):\n"
                + JsonSerializer.Serialize(priorResults.Select(result => new
                {
                    result.CallId,
                    result.ToolName,
                    result.Succeeded,
                    result.Output,
                    result.Error,
                }));
        var response = await DispatchGenerationAsync(
            new LocalCopilotSdkSession(session),
            new MessageOptions { Prompt = $"{systemPrompt}\n\n{input}{priorContext}" },
            diagnostics,
            cancellationToken);

        var responseStarted = Stopwatch.GetTimestamp();
        AddDiagnostic(diagnostics, "GenerationResponse", "Started", 0,
            "started", null, null, "true");
        LogStage("GenerationResponse", "Started", "started", null, 0, "true", null);
        try
        {
            dynamic dynamicResponse = response!;
            string? content = dynamicResponse?.Data?.Content;
            var providerRequestId = TryFindSafeRequestId(response);
            var actualModel = resolution.ConfirmedActualModel
                ?? await TryReadActualModelAsync(session, cancellationToken);
            AddDiagnostic(diagnostics, "GenerationResponse", "Completed",
                ElapsedMilliseconds(responseStarted), "completed", null,
                providerRequestId, "true");
            LogStage("GenerationResponse", "Completed", "completed", null,
                ElapsedMilliseconds(responseStarted), "true", providerRequestId);
            return new(content, calls, actualModel, providerRequestId, diagnostics, "true");
        }
        catch (Exception exception)
        {
            AddDiagnostic(diagnostics, "GenerationResponse", "Failed",
                ElapsedMilliseconds(responseStarted), "generation_response_unconfirmed",
                SafeExceptionType(exception), null, "unknown");
            LogStage("GenerationResponse", "Failed", "generation_response_unconfirmed",
                SafeExceptionType(exception), ElapsedMilliseconds(responseStarted), "unknown", null);
            throw Failure("GenerationResponse", "generation_response_unconfirmed",
                exception, "unknown", diagnostics);
        }
    }

    internal async Task<object?> DispatchGenerationAsync(
        ILocalCopilotSdkSession session,
        MessageOptions options,
        ICollection<ProviderOperationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        AddDiagnostic(diagnostics, "GenerationDispatch", "DispatchCallStarted", 0,
            "started", null, null, "unknown");
        LogStage("GenerationDispatch", "DispatchCallStarted", "started", null,
            0, "unknown", null);
        try
        {
            var response = await session.SendAndWaitAsync(options, cancellationToken);
            var duration = ElapsedMilliseconds(started);
            var requestId = TryFindSafeRequestId(response);
            AddDiagnostic(diagnostics, "GenerationDispatch", "TransportAccepted", duration,
                "accepted", null, requestId, "true");
            if (requestId is not null)
                AddDiagnostic(diagnostics, "GenerationDispatch", "ProviderRequestIdReceived",
                    duration, "received", null, requestId, "true");
            AddDiagnostic(diagnostics, "GenerationDispatch", "GenerationResponseReceived",
                duration, "completed", null, requestId, "true");
            LogStage("GenerationDispatch", "GenerationResponseReceived", "completed", null,
                duration, "true", requestId);
            return response;
        }
        catch (Exception exception)
        {
            var failure = ClassifyDispatchFailure(exception);
            var duration = ElapsedMilliseconds(started);
            if (failure.GenerationDispatched == "true")
                AddDiagnostic(diagnostics, "GenerationDispatch", "TransportAccepted", duration,
                    "accepted", null, failure.ProviderRequestId, "true", failure.SafeMetadata);
            AddDiagnostic(diagnostics, "GenerationDispatch", failure.Marker, duration,
                failure.ErrorCode, failure.SafeExceptionType, failure.ProviderRequestId,
                failure.GenerationDispatched, failure.SafeMetadata);
            LogStage("GenerationDispatch", failure.Marker, failure.ErrorCode,
                failure.SafeExceptionType, duration, failure.GenerationDispatched,
                failure.ProviderRequestId);
            throw new LocalCopilotRuntimeException(
                "GenerationDispatch",
                failure.ErrorCode,
                failure.SafeExceptionType,
                failure.GenerationDispatched,
                diagnostics.ToArray(),
                exception);
        }
    }

    internal static LocalCopilotDispatchFailure ClassifyDispatchFailure(Exception exception)
    {
        var remote = FindRemoteRpcException(exception);
        if (remote is not null)
        {
            var accepted = HasRemoteAcceptanceEvidence(remote.ErrorData);
            var code = GenerationRemoteResultCode(remote);
            return new(code,
                accepted ? "TransportRejectedAfterAcceptance" : "DispatchFailedBeforeAcceptance",
                accepted ? "true" : "false",
                nameof(RemoteRpcException),
                SafeRemoteRequestId(remote.ErrorData),
                $"rpcCode={remote.ErrorCode}");
        }
        return exception switch
        {
            RuntimeBinderException => new("dispatch_contract_invalid",
                "DispatchFailedBeforeAcceptance", "false", nameof(RuntimeBinderException)),
            ArgumentException => new("dispatch_request_invalid",
                "DispatchFailedBeforeAcceptance", "false", nameof(ArgumentException)),
            UnauthorizedAccessException => new("generation_authentication_rejected",
                "DispatchFailedBeforeAcceptance", "false", nameof(UnauthorizedAccessException)),
            TimeoutException => new("generation_outcome_ambiguous",
                "OutcomeUnknownAfterDispatch", "unknown", nameof(TimeoutException)),
            OperationCanceledException => new("generation_outcome_ambiguous",
                "OutcomeUnknownAfterDispatch", "unknown", nameof(OperationCanceledException)),
            IOException => new("generation_outcome_ambiguous",
                "OutcomeUnknownAfterDispatch", "unknown", nameof(IOException)),
            InvalidOperationException => new("generation_outcome_ambiguous",
                "OutcomeUnknownAfterDispatch", "unknown", nameof(InvalidOperationException)),
            _ => new("generation_outcome_ambiguous",
                "OutcomeUnknownAfterDispatch", "unknown", SafeExceptionType(exception)),
        };
    }

    private static string GenerationRemoteResultCode(RemoteRpcException remote) =>
        SafeRemoteValue(remote.ErrorData) switch
        {
            "unauthorized" or "authentication_required" or "invalid_credential" =>
                "generation_authentication_rejected",
            "model_invalid" or "invalid_model" or "model_not_found" or "model_unavailable" =>
                "generation_model_rejected",
            "rate_limited" or "too_many_requests" => "generation_rate_limited",
            "policy_blocked" or "policy_rejected" => "generation_policy_rejected",
            "invalid_params" or "invalid_request" or "configuration_invalid" =>
                "generation_configuration_rejected",
            _ when remote.ErrorCode == -32602 => "generation_configuration_rejected",
            _ => "generation_rpc_rejected",
        };

    private static bool HasRemoteAcceptanceEvidence(JsonElement? errorData) =>
        errorData is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("completionEventId", out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static string? SafeRemoteRequestId(JsonElement? errorData)
    {
        if (errorData is not { ValueKind: JsonValueKind.Object } data) return null;
        foreach (var name in new[] { "requestId", "messageId" })
        {
            if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                continue;
            var candidate = value.GetString();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 256
                && candidate.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.' or ':' or '/' or '@'))
                return candidate;
        }
        return null;
    }

    private static string? SafeRemoteValue(JsonElement? errorData)
    {
        if (errorData is not { ValueKind: JsonValueKind.Object } data) return null;
        foreach (var propertyName in new[] { "code", "category", "kind", "type" })
            if (data.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
                return value.GetString()?.Trim().ToLowerInvariant();
        return null;
    }

    internal static LocalCopilotModelResolution ResolveModel(
        string requestedModel,
        IReadOnlyCollection<ProviderModelDto> models,
        ICollection<ProviderOperationDiagnostic>? diagnostics = null)
    {
        var started = Stopwatch.GetTimestamp();
        AddDiagnostic(diagnostics, "ModelResolution", "Started", 0,
            "started", null, null, "false");
        if (requestedModel.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var concrete = models
                .Where(candidate => !candidate.Id.Equals("auto", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var result = concrete.Length == 1
                ? new LocalCopilotModelResolution(concrete[0].Id, concrete[0].Id)
                : new LocalCopilotModelResolution("auto", null);
            AddDiagnostic(diagnostics, "ModelResolution", "Completed",
                ElapsedMilliseconds(started), "resolved", null, null, "false");
            return result;
        }
        var exact = models.SingleOrDefault(candidate =>
            candidate.Id.Equals(requestedModel, StringComparison.Ordinal));
        if (exact is null)
        {
            AddDiagnostic(diagnostics, "ModelResolution", "Failed",
                ElapsedMilliseconds(started), "model_not_available",
                nameof(LocalCopilotModelCatalogUnavailableException), null, "false");
            throw Failure("ModelResolution", "model_not_available",
                new LocalCopilotModelCatalogUnavailableException(), "false", diagnostics ?? []);
        }
        AddDiagnostic(diagnostics, "ModelResolution", "Completed",
            ElapsedMilliseconds(started), "resolved", null, null, "false");
        return new(exact.Id, exact.Id);
    }

    private async Task<T> RunStageAsync<T>(
        string operation,
        string generationDispatched,
        ICollection<ProviderOperationDiagnostic> diagnostics,
        Func<Task<T>> action,
        string? safeMetadata = null)
    {
        var started = Stopwatch.GetTimestamp();
        AddDiagnostic(diagnostics, operation, "Started", 0, "started",
            null, null, generationDispatched, safeMetadata);
        LogStage(operation, "Started", "started", null, 0, generationDispatched, null);
        try
        {
            var result = await action();
            AddDiagnostic(diagnostics, operation, "Completed", ElapsedMilliseconds(started),
                "completed", null, null, generationDispatched, safeMetadata);
            LogStage(operation, "Completed", "completed", null,
                ElapsedMilliseconds(started), generationDispatched, null);
            return result;
        }
        catch (LocalCopilotRuntimeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = ResultCode(operation, exception);
            var failureMetadata = operation == "SessionCreate"
                ? AppendSafeMetadata(safeMetadata, SessionCreateErrorMetadata(exception))
                : safeMetadata;
            AddDiagnostic(diagnostics, operation, "Failed", ElapsedMilliseconds(started),
                code, SafeExceptionType(exception), null, generationDispatched, failureMetadata);
            LogStage(operation, "Failed", code, SafeExceptionType(exception),
                ElapsedMilliseconds(started), generationDispatched, null);
            throw Failure(operation, code, exception, generationDispatched, diagnostics);
        }
    }

    private void LogStage(
        string operation,
        string marker,
        string resultCode,
        string? safeExceptionType,
        long durationMilliseconds,
        string generationDispatched,
        string? providerRequestId)
    {
        var correlationId = Activity.Current?.TraceId.ToString();
        if (safeExceptionType is null)
            LogStageCompleted(logger, operation, marker, resultCode, durationMilliseconds,
                generationDispatched, correlationId, providerRequestId);
        else
            LogStageFailed(logger, operation, marker, resultCode, safeExceptionType,
                durationMilliseconds, generationDispatched, correlationId, providerRequestId);
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Information,
        Message = "Local Copilot SDK operation {Operation} {Marker}; resultCode={ResultCode}; durationMs={DurationMilliseconds}; generationDispatched={GenerationDispatched}; correlationId={CorrelationId}; providerRequestId={ProviderRequestId}")]
    private static partial void LogStageCompleted(ILogger logger, string operation,
        string marker, string resultCode, long durationMilliseconds,
        string generationDispatched, string? correlationId, string? providerRequestId);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning,
        Message = "Local Copilot SDK operation {Operation} {Marker}; resultCode={ResultCode}; safeExceptionType={SafeExceptionType}; durationMs={DurationMilliseconds}; generationDispatched={GenerationDispatched}; correlationId={CorrelationId}; providerRequestId={ProviderRequestId}")]
    private static partial void LogStageFailed(ILogger logger, string operation,
        string marker, string resultCode, string safeExceptionType, long durationMilliseconds,
        string generationDispatched, string? correlationId, string? providerRequestId);

    private static LocalCopilotRuntimeException Failure(
        string operation,
        string code,
        Exception exception,
        string generationDispatched,
        IEnumerable<ProviderOperationDiagnostic> diagnostics) =>
        new(operation, code, SafeExceptionType(exception), generationDispatched,
            diagnostics.ToArray(), exception);

    private static string ResultCode(string operation, Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "connection_requires_reconnect",
            LocalCopilotModelCatalogUnavailableException => operation == "ModelResolution"
                ? "model_not_available" : "model_catalog_unavailable",
            OperationCanceledException => operation == "RuntimeStart"
                ? "runtime_start_unavailable" : $"{operation.ToLowerInvariant()}_timeout",
            HttpRequestException or TimeoutException => operation == "RuntimeStart"
                ? "runtime_start_unavailable" : $"{operation.ToLowerInvariant()}_unavailable",
            _ when operation == "SessionCreate" => SessionCreateResultCode(exception),
            _ => operation switch
            {
                "Authentication" => "authentication_unavailable",
                "ModelCatalog" => "model_catalog_unavailable",
                _ => "local_copilot_runtime_failure",
            },
        };

    internal static string SessionCreateResultCode(Exception exception)
    {
        var remote = FindRemoteRpcException(exception);
        if (remote is not null)
        {
            var category = SafeRemoteCategory(remote.ErrorData);
            if (category is not null) return category;
            if (remote.ErrorCode == -32601)
                return "session_runtime_incompatible";
            if (remote.ErrorCode == -32602)
                return "session_option_unsupported";
            return "session_create_protocol_error";
        }
        return exception switch
        {
            UnauthorizedAccessException => "session_connection_rejected",
            ArgumentException or InvalidOperationException => "session_configuration_invalid",
            IOException or JsonException => "session_create_protocol_error",
            _ => "session_create_failed_unknown",
        };
    }

    private static string? SafeRemoteCategory(JsonElement? errorData)
    {
        if (errorData is not { ValueKind: JsonValueKind.Object } data) return null;
        foreach (var propertyName in new[] { "code", "category", "kind", "type" })
        {
            if (!data.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String) continue;
            return value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "model_required" or "missing_model" => "session_model_required",
                "model_invalid" or "invalid_model" or "model_not_found" => "session_model_invalid",
                "unsupported_option" or "invalid_session_option" => "session_option_unsupported",
                "runtime_incompatible" or "protocol_version_mismatch" => "session_runtime_incompatible",
                "connection_rejected" or "unauthorized" => "session_connection_rejected",
                _ => null,
            };
        }
        return null;
    }

    private static string SafeExceptionType(Exception exception) => exception switch
    {
        UnauthorizedAccessException => nameof(UnauthorizedAccessException),
        OperationCanceledException => nameof(OperationCanceledException),
        TimeoutException => nameof(TimeoutException),
        HttpRequestException => nameof(HttpRequestException),
        JsonException => nameof(JsonException),
        LocalCopilotModelCatalogUnavailableException => nameof(LocalCopilotModelCatalogUnavailableException),
        InvalidOperationException => nameof(InvalidOperationException),
        _ when FindRemoteRpcException(exception) is not null => nameof(RemoteRpcException),
        IOException => nameof(IOException),
        ArgumentException => nameof(ArgumentException),
        _ when exception.GetType().Name.Contains("Rpc", StringComparison.Ordinal) => "RpcException",
        _ => "ProviderRuntimeException",
    };

    private static RemoteRpcException? FindRemoteRpcException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is RemoteRpcException remote) return remote;
        return null;
    }

    private static string? SessionCreateErrorMetadata(Exception exception)
    {
        var remote = FindRemoteRpcException(exception);
        return remote is null ? null : $"rpcCode={remote.ErrorCode}";
    }

    private static string? AppendSafeMetadata(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ? right
            : string.IsNullOrWhiteSpace(right) ? left : $"{left};{right}";

    private static void AddDiagnostic(
        ICollection<ProviderOperationDiagnostic>? diagnostics,
        string operation,
        string marker,
        long durationMilliseconds,
        string resultCode,
        string? safeExceptionType,
        string? providerRequestId,
        string generationDispatched,
        string? safeMetadata = null)
    {
        var correlationId = Activity.Current?.TraceId.ToString();
        diagnostics?.Add(new(operation, marker, durationMilliseconds, resultCode,
            safeExceptionType, correlationId, providerRequestId, generationDispatched,
            safeMetadata));
    }

    private static long ElapsedMilliseconds(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static string? TryFindSafeRequestId(object? response)
    {
        try
        {
            var value = response is null ? null
                : FindString(JsonSerializer.SerializeToElement(response), "requestId");
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Length <= 256 && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':' or '/' or '@'))
                return value;
            return $"request:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string?> TryReadActualModelAsync(
        dynamic session,
        CancellationToken cancellationToken)
    {
        try
        {
            dynamic current = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
            string? planBaseModel = current?.PlanBaseModelId;
            string? currentModel = current?.ModelId;
            if (!string.IsNullOrWhiteSpace(planBaseModel)
                && !planBaseModel.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return planBaseModel;
            return string.IsNullOrWhiteSpace(currentModel)
                || currentModel.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? null : currentModel;
        }
        catch
        {
            return null;
        }
    }

    private CopilotClient CreateClient() => new(CreateClientOptions(options));

    internal static CopilotClientOptions CreateClientOptions(LocalCopilotCliOptions options) => new()
    {
        Connection = RuntimeConnection.ForUri(options.Endpoint, options.ConnectionToken),
        Mode = CopilotClientMode.Empty,
        LogLevel = CopilotLogLevel.Warning,
    };

    internal static SessionConfig BuildSessionConfig(
        string? model,
        IReadOnlyCollection<ToolDefinition> tools,
        List<ToolCallRequest> calls) => new()
        {
            Model = model,
            Streaming = false,
            Tools = tools.Select(definition => CreateTool(definition, calls)).ToArray(),
            AvailableTools = BuildAvailableTools(tools),
            EnableSessionStore = false,
            WorkingDirectory = "/tmp/aicontrolcenter-session",
            TrustWorkingDirectory = WorkingDirectoryTrust.Session,
        };

    internal static string SessionConfigurationFingerprint(
        SessionConfig config,
        string? connectionToken)
    {
        var workingDirectoryHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(config.WorkingDirectory ?? string.Empty))).ToLowerInvariant();
        return string.Join(';',
            $"model={(config.Model is null ? "omitted" : config.Model)}",
            $"systemMessage={(config.SystemMessage is null ? "omitted" : "configured")}",
            $"tools={config.Tools?.Count ?? 0}",
            $"availableTools={config.AvailableTools?.Count ?? 0}",
            $"streaming={(config.Streaming is true ? "enabled" : "omitted")}",
            $"workingDirectory=sha256:{workingDirectoryHash}",
            $"sessionId={(config.SessionId is null ? "generated" : "provided")}",
            "clientMode=empty",
            "runtimeConnection=uri",
            $"connectionToken={(string.IsNullOrEmpty(connectionToken) ? "absent" : "present")}",
            $"sessionStore={(config.EnableSessionStore is true ? "enabled" : "disabled")}",
            $"trustWorkingDirectory={config.TrustWorkingDirectory?.ToString().ToLowerInvariant() ?? "omitted"}");
    }

    internal static ToolSet BuildAvailableTools(IReadOnlyCollection<ToolDefinition> tools)
    {
        var available = new ToolSet();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in tools)
        {
            var wireName = CanonicalToolName(definition.Name);
            if (!names.Add(wireName))
                throw new InvalidOperationException("Configured tool names are not unique after canonicalization.");
            available.AddCustom(wireName);
        }
        return available;
    }

    internal static string CanonicalToolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Configured tool name is empty.");
        var characters = name.Select(character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' ? character : '_').ToArray();
        var result = new string(characters);
        return result.Length is > 0 and <= 128
            ? result
            : throw new InvalidOperationException("Configured tool name cannot be represented safely.");
    }

    private static async Task<IReadOnlyCollection<ProviderModelDto>> ReadModelsAsync(
        CopilotClient client,
        CancellationToken cancellationToken)
    {
        var models = await client.ListModelsAsync(cancellationToken);
        return models.Select(model => new ProviderModelDto(
            model.Id, model.Name ?? model.Id, null, true, false, null, null)).ToArray();
    }

    private static string? FindString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindString(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static AIFunction CreateTool(ToolDefinition definition, List<ToolCallRequest> calls)
    {
        var toolOptions = new CopilotToolOptions
        {
            SkipPermission = true,
            Defer = CopilotToolDefer.Never,
        };
        var factoryOptions = new AIFunctionFactoryOptions
        {
            Name = CanonicalToolName(definition.Name),
            Description = definition.Description,
        };
        object Deferred(ToolInvocation invocation)
        {
            calls.Add(new(invocation.ToolCallId, definition.Name,
                JsonSerializer.Serialize(invocation.Arguments)));
            return new { deferred = true, callId = invocation.ToolCallId };
        }
        return definition.Name switch
        {
            "text.length" => CopilotTool.DefineTool(
                (string text, ToolInvocation invocation) => Deferred(invocation),
                toolOptions, factoryOptions),
            _ => CopilotTool.DefineTool(
                (ToolInvocation invocation) => Deferred(invocation),
                toolOptions, factoryOptions),
        };
    }
}

internal sealed record LocalCopilotModelResolution(string? SessionModel, string? ConfirmedActualModel);

internal sealed class LocalCopilotModelCatalogUnavailableException : InvalidOperationException;
