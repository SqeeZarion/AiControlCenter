namespace AiControlCenter.LocalCopilot.Sidecar;

public static class LocalCopilotOperationStateMachine
{
    public static bool IsActive(ReconnectState state) => state is
        ReconnectState.Starting or ReconnectState.StartingLogin
        or ReconnectState.WaitingForUser or ReconnectState.GitHubAuthorized
        or ReconnectState.PersistingCredential or ReconnectState.RestartingRuntime
        or ReconnectState.Validating or ReconnectState.SynchronizingModels
        or ReconnectState.Cancelling;

    public static bool IsTerminal(ReconnectState state) => !IsActive(state);

    public static bool CanCancel(ReconnectState state) => state is
        ReconnectState.Starting or ReconnectState.StartingLogin or ReconnectState.WaitingForUser;

    public static bool ExposesDeviceCode(ReconnectState state) => state == ReconnectState.WaitingForUser;

    public static TimeSpan TimeoutFor(ReconnectState state) => state switch
    {
        ReconnectState.Starting or ReconnectState.StartingLogin => TimeSpan.FromMinutes(2),
        ReconnectState.WaitingForUser => TimeSpan.FromMinutes(15),
        ReconnectState.GitHubAuthorized or ReconnectState.PersistingCredential => TimeSpan.FromMinutes(1),
        ReconnectState.RestartingRuntime => TimeSpan.FromMinutes(1),
        ReconnectState.Validating or ReconnectState.SynchronizingModels => TimeSpan.FromMinutes(2),
        ReconnectState.Cancelling => TimeSpan.FromSeconds(15),
        _ => TimeSpan.Zero,
    };

    public static bool CanTransition(ReconnectState current, ReconnectState next) => (current, next) switch
    {
        (ReconnectState.StartingLogin, ReconnectState.WaitingForUser) => true,
        (ReconnectState.StartingLogin, ReconnectState.GitHubAuthorized) => true,
        (ReconnectState.StartingLogin, ReconnectState.LoginFailed) => true,
        (ReconnectState.Starting, ReconnectState.WaitingForUser) => true,
        (ReconnectState.Starting, ReconnectState.GitHubAuthorized) => true,
        (ReconnectState.Starting, ReconnectState.AuthorizationFailed) => true,
        (ReconnectState.WaitingForUser, ReconnectState.GitHubAuthorized) => true,
        (ReconnectState.WaitingForUser, ReconnectState.LoginFailed) => true,
        (ReconnectState.WaitingForUser, ReconnectState.AuthorizationFailed) => true,
        (ReconnectState.GitHubAuthorized, ReconnectState.PersistingCredential) => true,
        (ReconnectState.GitHubAuthorized, ReconnectState.RestartingRuntime) => true,
        (ReconnectState.PersistingCredential, ReconnectState.RestartingRuntime) => true,
        (ReconnectState.RestartingRuntime, ReconnectState.Validating) => true,
        (ReconnectState.RestartingRuntime, ReconnectState.RuntimeRestartFailed) => true,
        (ReconnectState.Validating, ReconnectState.SynchronizingModels) => true,
        (ReconnectState.Validating, ReconnectState.ValidationFailed) => true,
        (ReconnectState.Validating, ReconnectState.ModelSyncFailed) => true,
        (ReconnectState.SynchronizingModels, ReconnectState.Authorized) => true,
        (ReconnectState.SynchronizingModels, ReconnectState.ModelSyncFailed) => true,
        (_, ReconnectState.Cancelling) when CanCancel(current) => true,
        (ReconnectState.Cancelling, ReconnectState.Cancelled) => true,
        (_, ReconnectState.Expired) when IsActive(current) => true,
        _ => false,
    };

    public static string ExpirationCode(ReconnectState state) => state switch
    {
        ReconnectState.WaitingForUser => "DeviceCodeExpired",
        ReconnectState.Validating => "ValidationTimeout",
        ReconnectState.SynchronizingModels => "ModelCatalogTimeout",
        ReconnectState.RestartingRuntime => "RuntimeRestartTimeout",
        ReconnectState.Cancelling => "CancellationTimeout",
        _ => "OperationExpired",
    };
}
