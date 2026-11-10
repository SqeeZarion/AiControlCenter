using Microsoft.Extensions.Logging;

namespace AiControlCenter.LocalCopilot.Sidecar;

public sealed partial class ReconnectOperationTracker(
    TimeProvider timeProvider,
    ILogger<ReconnectOperationTracker>? logger = null)
{
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumStartInterval = TimeSpan.FromSeconds(2);
    private readonly object sync = new();
    private ReconnectOperationDto? operation;
    private DateTimeOffset lastStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastUpdatedAt = DateTimeOffset.MinValue;

    public (bool Created, ReconnectOperationDto Operation) TryStart(
        ReconnectState initialState = ReconnectState.Starting,
        Guid ownerId = default,
        string? correlationId = null)
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow();
            ExpireIfNeeded(now);
            if (operation is not null && LocalCopilotOperationStateMachine.IsActive(operation.State))
                return (false, operation);
            if (now - lastStartedAt < MinimumStartInterval)
                return (false, Create(Guid.NewGuid(), ReconnectState.AuthorizationFailed,
                    now.Add(MinimumStartInterval), "ReconnectRateLimited", ownerId, correlationId));

            lastStartedAt = now;
            operation = Create(Guid.NewGuid(), initialState,
                now.Add(LocalCopilotOperationStateMachine.TimeoutFor(initialState)),
                null, ownerId, correlationId);
            lastUpdatedAt = now;
            if (logger is not null)
                LogTransition(logger, operation.Id, null, initialState, null);
            return (true, operation);
        }
    }

    public ReconnectOperationDto? GetCurrent()
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow();
            ExpireIfNeeded(now);
            if (operation is not null && LocalCopilotOperationStateMachine.IsTerminal(operation.State)
                && now - lastUpdatedAt > TerminalRetention)
                operation = null;
            return operation;
        }
    }

    public ReconnectOperationDto? Get(Guid id)
    {
        lock (sync)
        {
            if (operation?.Id != id) return null;
            ExpireIfNeeded(timeProvider.GetUtcNow());
            return operation;
        }
    }

    public bool SetWaitingForUser(Guid id, string verificationUri, string userCode)
    {
        lock (sync)
        {
            if (operation?.Id != id || operation.State is not (
                ReconnectState.Starting or ReconnectState.StartingLogin)) return false;
            var previous = operation.State;
            operation = operation with
            {
                State = ReconnectState.WaitingForUser,
                VerificationUri = verificationUri,
                UserCode = userCode,
                ExpiresAt = timeProvider.GetUtcNow().Add(
                    LocalCopilotOperationStateMachine.TimeoutFor(ReconnectState.WaitingForUser)),
                CanCancel = true,
            };
            lastUpdatedAt = timeProvider.GetUtcNow();
            if (logger is not null)
                LogTransition(logger, id, previous,
                    ReconnectState.WaitingForUser, null);
            return true;
        }
    }

    public bool TryTransition(
        Guid id,
        ReconnectState state,
        string? errorCode,
        int? processExitCode = null,
        string? failureReason = null)
    {
        lock (sync)
        {
            if (operation?.Id != id || LocalCopilotOperationStateMachine.IsTerminal(operation.State)
                || !LocalCopilotOperationStateMachine.CanTransition(operation.State, state))
                return false;
            var previous = operation.State;
            operation = operation with
            {
                State = state,
                VerificationUri = state == ReconnectState.WaitingForUser
                    ? operation.VerificationUri : null,
                UserCode = state == ReconnectState.WaitingForUser
                    ? operation.UserCode : null,
                ErrorCode = errorCode,
                ExpiresAt = LocalCopilotOperationStateMachine.IsActive(state)
                    ? timeProvider.GetUtcNow().Add(LocalCopilotOperationStateMachine.TimeoutFor(state))
                    : operation.ExpiresAt,
                ProcessExitCode = processExitCode,
                FailureReason = failureReason,
                CanCancel = LocalCopilotOperationStateMachine.CanCancel(state),
            };
            lastUpdatedAt = timeProvider.GetUtcNow();
            if (logger is not null)
                LogTransition(logger, id, previous, state, errorCode);
            return true;
        }
    }

    public bool SetTerminal(Guid id, ReconnectState state, string? errorCode) =>
        TryTransition(id, state, errorCode);

    public bool BeginCancellation(Guid id)
    {
        lock (sync)
        {
            if (operation?.Id != id) return false;
            if (LocalCopilotOperationStateMachine.IsTerminal(operation.State)) return true;
            if (!LocalCopilotOperationStateMachine.CanTransition(
                    operation.State, ReconnectState.Cancelling)) return false;
            var previous = operation.State;
            operation = operation with
            {
                State = ReconnectState.Cancelling,
                VerificationUri = null,
                UserCode = null,
                ErrorCode = null,
                ExpiresAt = timeProvider.GetUtcNow().Add(
                    LocalCopilotOperationStateMachine.TimeoutFor(ReconnectState.Cancelling)),
                CanCancel = false,
            };
            lastUpdatedAt = timeProvider.GetUtcNow();
            if (logger is not null)
                LogTransition(logger, id, previous, ReconnectState.Cancelling, null);
            return true;
        }
    }

    public bool CompleteCancellation(Guid id) =>
        TryTransition(id, ReconnectState.Cancelled, null, null, "CancelledByUser");

    public bool Cancel(Guid id) => BeginCancellation(id) && CompleteCancellation(id);

    private void ExpireIfNeeded(DateTimeOffset now)
    {
        if (operation is not null && LocalCopilotOperationStateMachine.IsActive(operation.State)
            && now >= operation.ExpiresAt)
        {
            var previous = operation.State;
            var code = LocalCopilotOperationStateMachine.ExpirationCode(previous);
            operation = operation with
            {
                State = ReconnectState.Expired,
                VerificationUri = null,
                UserCode = null,
                ErrorCode = code,
                FailureReason = "OperationTimedOut",
                CanCancel = false,
            };
            lastUpdatedAt = now;
            if (logger is not null)
                LogTransition(logger, operation.Id, previous,
                    ReconnectState.Expired, code);
        }
    }

    private static ReconnectOperationDto Create(
        Guid id,
        ReconnectState state,
        DateTimeOffset expiresAt,
        string? errorCode,
        Guid ownerId,
        string? correlationId) => new(
            id, state, null, null, expiresAt, errorCode, ownerId,
            NormalizeCorrelationId(correlationId), null, null,
            LocalCopilotOperationStateMachine.CanCancel(state));

    private static string? NormalizeCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.').Take(128).ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    [LoggerMessage(EventId = 6200, Level = LogLevel.Information,
        Message = "Local Copilot reconnect {OperationId} changed from {PreviousState} to {State} with {ErrorCode}.")]
    private static partial void LogTransition(
        ILogger logger, Guid operationId, ReconnectState? previousState,
        ReconnectState state, string? errorCode);
}
