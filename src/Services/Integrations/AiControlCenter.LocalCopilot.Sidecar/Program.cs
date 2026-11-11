using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AiControlCenter.LocalCopilot.Sidecar;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://0.0.0.0:4322");
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ReconnectOperationTracker>();
        builder.Services.AddSingleton<ICopilotLoginProcessRunner, SystemCopilotLoginProcessRunner>();
        builder.Services.AddSingleton<ICopilotCliRuntimeHost, CopilotCliRuntimeHost>();
        builder.Services.AddSingleton<CopilotSupervisor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CopilotSupervisor>());

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var expected = Environment.GetEnvironmentVariable("COPILOT_CONNECTION_TOKEN") ?? string.Empty;
            var supplied = context.Request.Headers["X-AiControlCenter-Local-Copilot-Token"].ToString();
            if (expected.Length < 32 || !FixedTimeEquals(expected, supplied))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapGet("/health", (CopilotSupervisor supervisor) => supervisor.IsHeadlessReady
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/v1/capabilities", () => Results.Ok(new
        {
            safeReauthentication = false,
            nonInteractiveLogout = false,
            bundledCliVersion = "1.0.103",
        }));
        app.MapPost("/v1/reconnect", async (StartReconnectRequest request,
            CopilotSupervisor supervisor, CancellationToken token) =>
        {
            var result = await supervisor.StartReconnectAsync(
                request.OwnerId, request.CorrelationId, token);
            return result.Created ? Results.Accepted($"/v1/reconnect/{result.Operation.Id}", result.Operation)
                : Results.Conflict(result.Operation);
        });
        app.MapPost("/v1/reauthenticate", async (ForceReauthenticateRequest request,
            CopilotSupervisor supervisor, CancellationToken token) =>
        {
            if (!request.Confirmed)
                return Results.BadRequest(new { code = "ReauthenticationConfirmationRequired" });
            return Results.Conflict(new { code = "LocalCopilotReauthenticationUnsupported" });
        });
        app.MapPost("/v1/logout", async (LocalCopilotLogoutRequest request,
            CopilotSupervisor supervisor, CancellationToken token) =>
        {
            if (!request.Confirmed)
                return Results.BadRequest(new { code = "LocalLogoutConfirmationRequired" });
            return Results.Conflict(new
            {
                status = "Unsupported",
                errorCode = "LocalCopilotLogoutUnsupported",
            });
        });
        app.MapGet("/v1/reconnect/{id:guid}", (Guid id, CopilotSupervisor supervisor) =>
            supervisor.Get(id) is { } operation ? Results.Ok(operation) : Results.NotFound());
        app.MapGet("/v1/reconnect/current", (CopilotSupervisor supervisor) =>
            supervisor.GetCurrent() is { } operation ? Results.Ok(operation) : Results.NoContent());
        app.MapPost("/v1/reconnect/{id:guid}/cancel", async (Guid id, CopilotSupervisor supervisor) =>
            await supervisor.CancelAsync(id) ? Results.Ok(supervisor.Get(id)) : Results.NotFound());
        app.MapPost("/v1/reconnect/{id:guid}/state", (Guid id, ReconnectStateUpdate request,
            CopilotSupervisor supervisor) => supervisor.TryTransition(id, request.State, request.ErrorCode)
                ? Results.Ok(supervisor.Get(id))
                : Results.Conflict(supervisor.Get(id)));
        await app.RunAsync();

        static bool FixedTimeEquals(string expected, string supplied)
        {
            var left = Encoding.UTF8.GetBytes(expected);
            var right = Encoding.UTF8.GetBytes(supplied);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
    }
}

public enum ReconnectState
{
    Starting,
    StartingLogin,
    WaitingForUser,
    GitHubAuthorized,
    PersistingCredential,
    RestartingRuntime,
    Validating,
    SynchronizingModels,
    Cancelling,
    Authorized,
    LoginFailed,
    AuthorizationFailed,
    RuntimeRestartFailed,
    ValidationFailed,
    ModelSyncFailed,
    Expired,
    Cancelled,
}

public enum ReconnectMode
{
    ValidateExisting,
    ForceReauthenticate,
}

public sealed record ReconnectOperationDto(Guid Id, ReconnectState State, string? VerificationUri,
    string? UserCode, DateTimeOffset ExpiresAt, string? ErrorCode,
    Guid OwnerId = default, string? CorrelationId = null, int? ProcessExitCode = null,
    string? FailureReason = null, bool CanCancel = false);
public sealed record ReconnectStateUpdate(ReconnectState State, string? ErrorCode);
public sealed record StartReconnectRequest(Guid OwnerId, string? CorrelationId);
public sealed record ForceReauthenticateRequest(
    bool Confirmed, Guid OwnerId = default, string? CorrelationId = null);
public sealed record LocalCopilotLogoutRequest(bool Confirmed);
public sealed record LocalCopilotLogoutDto(string Status, string? ErrorCode);

public sealed partial class CopilotSupervisor(
    ILogger<CopilotSupervisor> logger,
    ReconnectOperationTracker operations,
    ICopilotCliRuntimeHost runtime) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? operationCancellation;
    private Task? activeOperationTask;

    public bool IsHeadlessReady => runtime.IsHeadlessReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await runtime.RestartHeadlessAsync(stoppingToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public ReconnectOperationDto? Get(Guid id)
    {
        var current = operations.Get(id);
        if (current?.State == ReconnectState.Expired)
        {
            operationCancellation?.Cancel();
            runtime.StopLogin();
        }
        return current;
    }

    public ReconnectOperationDto? GetCurrent() => operations.GetCurrent();

    public async Task<LocalCopilotLogoutDto> EndLocalSessionAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var current = operations.GetCurrent();
            if (current is not null && IsActiveOperation(current.State))
                return new("Busy", "ReconnectOperationActive");

            await runtime.StopHeadlessAsync(token);
            var logout = await runtime.LogoutAsync(token);
            if (logout.Status is CopilotLogoutStatus.Succeeded
                or CopilotLogoutStatus.AlreadyUnauthenticated)
                return new(logout.Status.ToString(), null);

            if (logout.CredentialPresent)
            {
                try { await runtime.RestartHeadlessAsync(token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { LogReconnectFailure(logger, Guid.Empty, "LogoutRecovery", exception.GetType().Name); }
            }
            var code = logout.Status switch
            {
                CopilotLogoutStatus.UnsupportedCommand => "CopilotLogoutUnsupported",
                CopilotLogoutStatus.TimedOut => "CopilotLogoutTimeout",
                _ => "CopilotLogoutFailed",
            };
            return new("Failed", code);
        }
        finally { gate.Release(); }
    }

    public async Task<(bool Created, ReconnectOperationDto Operation)> StartReconnectAsync(
        CancellationToken token) => await StartReconnectAsync(
            ReconnectMode.ValidateExisting, Guid.Empty, null, confirmed: false, token);

    public async Task<(bool Created, ReconnectOperationDto Operation)> StartReconnectAsync(
        Guid ownerId, string? correlationId, CancellationToken token) => await StartReconnectAsync(
            ReconnectMode.ValidateExisting, ownerId, correlationId, confirmed: false, token);

    public async Task<(bool Created, ReconnectOperationDto Operation)> StartReconnectAsync(
        ReconnectMode mode,
        bool confirmed,
        CancellationToken token) => await StartReconnectAsync(
            mode, Guid.Empty, null, confirmed, token);

    public async Task<(bool Created, ReconnectOperationDto Operation)> StartReconnectAsync(
        ReconnectMode mode,
        Guid ownerId,
        string? correlationId,
        bool confirmed,
        CancellationToken token)
    {
        if (mode == ReconnectMode.ForceReauthenticate && !confirmed)
            throw new ArgumentException("Explicit confirmation is required.", nameof(confirmed));
        await gate.WaitAsync(token);
        try
        {
            var recoverStoredSession = mode == ReconnectMode.ValidateExisting
                && runtime.HasStoredCredential;
            var initialState = recoverStoredSession
                ? ReconnectState.GitHubAuthorized
                : ReconnectState.StartingLogin;
            var result = operations.TryStart(initialState, ownerId, correlationId);
            if (!result.Created) return result;
            operationCancellation?.Dispose();
            operationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            if (mode == ReconnectMode.ForceReauthenticate)
            {
                activeOperationTask = RunLoginAsync(result.Operation.Id, operationCancellation.Token);
                return result;
            }
            activeOperationTask = recoverStoredSession
                ? RecoverStoredSessionAsync(result.Operation.Id, operationCancellation.Token)
                : RunLoginAsync(result.Operation.Id, operationCancellation.Token);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> CancelAsync(Guid id)
    {
        var current = operations.Get(id);
        if (current is null) return false;
        if (!LocalCopilotOperationStateMachine.CanCancel(current.State)) return true;
        if (!operations.BeginCancellation(id)) return false;
        operationCancellation?.Cancel();
        runtime.StopLogin();
        if (activeOperationTask is { } task)
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        operations.CompleteCancellation(id);
        return true;
    }

    private async Task RunLoginAsync(Guid id, CancellationToken token)
    {
        try
        {
            var result = await runtime.LoginAsync(
                prompt => operations.SetWaitingForUser(id, prompt.VerificationUri, prompt.UserCode), token);
            if (result.ExitCode != 0 || !result.CredentialPresent)
            {
                operations.TryTransition(id, ReconnectState.LoginFailed,
                    result.ExitCode == 0 ? "CredentialNotPersisted" : "CopilotLoginFailed",
                    result.ExitCode, "LoginProcessExited");
                return;
            }
            if (!operations.TryTransition(id, ReconnectState.GitHubAuthorized, null)) return;
            if (!operations.TryTransition(id, ReconnectState.PersistingCredential, null)) return;
            await RestartAndPrepareValidationAsync(id, token);
        }
        catch (OperationCanceledException)
        {
            if (operations.Get(id)?.State != ReconnectState.Cancelling)
                operations.TryTransition(id, ReconnectState.Expired, "DeviceCodeExpired");
        }
        catch (Exception exception)
        {
            LogReconnectFailure(logger, id, "Authorization", exception.GetType().Name);
            operations.TryTransition(id, ReconnectState.LoginFailed, "CopilotLoginUnavailable");
        }
    }

    private async Task RecoverStoredSessionAsync(Guid id, CancellationToken token)
    {
        try
        {
            if (operations.Get(id)?.State != ReconnectState.GitHubAuthorized) return;
            await RestartAndPrepareValidationAsync(id, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (operations.Get(id)?.State != ReconnectState.Cancelling)
                operations.TryTransition(id, ReconnectState.Expired, "ReconnectExpired");
        }
        catch (Exception exception)
        {
            LogReconnectFailure(logger, id, "RuntimeRestart", exception.GetType().Name);
            operations.TryTransition(id, ReconnectState.RuntimeRestartFailed, "RuntimeRestartFailed");
        }
    }

    private async Task RestartAndPrepareValidationAsync(Guid id, CancellationToken token)
    {
        if (!operations.TryTransition(id, ReconnectState.RestartingRuntime, null)) return;
        try
        {
            await runtime.RestartHeadlessAsync(token);
            operations.TryTransition(id, ReconnectState.Validating, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReconnectFailure(logger, id, "RuntimeRestart", exception.GetType().Name);
            operations.TryTransition(id, ReconnectState.RuntimeRestartFailed, "RuntimeRestartFailed");
        }
    }

    public bool TryTransition(Guid id, ReconnectState state, string? errorCode)
        => operations.TryTransition(id, state, errorCode);

    public override void Dispose()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        runtime.StopLogin();
        gate.Dispose();
        base.Dispose();
    }

    [LoggerMessage(EventId = 6201, Level = LogLevel.Warning,
        Message = "Local Copilot reconnect {OperationId} failed during {Stage} with {ExceptionType}.")]
    private static partial void LogReconnectFailure(
        ILogger logger, Guid operationId, string stage, string exceptionType);

    private static bool IsActiveOperation(ReconnectState state) =>
        LocalCopilotOperationStateMachine.IsActive(state);
}
