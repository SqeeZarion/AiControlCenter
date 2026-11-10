using AiControlCenter.LocalCopilot.Sidecar;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class LocalCopilotReconnectTrackerTests
{
    [Fact]
    public void OnlyOneActiveOperationCanExist()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());

        var first = tracker.TryStart();
        var duplicate = tracker.TryStart();

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Operation.Id, duplicate.Operation.Id);
        Assert.Equal(ReconnectState.Starting, duplicate.Operation.State);
    }

    [Fact]
    public void WaitingOperationExpiresAtBoundedDeadline()
    {
        var clock = new MutableTimeProvider();
        var tracker = new ReconnectOperationTracker(clock);
        var started = tracker.TryStart().Operation;
        Assert.True(tracker.SetWaitingForUser(started.Id,
            "https://github.com/login/device", "ABCD-EFGH"));

        clock.Advance(TimeSpan.FromMinutes(15));
        var expired = tracker.Get(started.Id);

        Assert.NotNull(expired);
        Assert.Equal(ReconnectState.Expired, expired.State);
        Assert.Equal("DeviceCodeExpired", expired.ErrorCode);
    }

    [Fact]
    public void CancelledOperationCannotBeReactivatedByLateProcessCompletion()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var started = tracker.TryStart().Operation;

        Assert.True(tracker.Cancel(started.Id));
        Assert.False(tracker.TryTransition(started.Id, ReconnectState.GitHubAuthorized, null));

        var cancelled = tracker.Get(started.Id);
        Assert.NotNull(cancelled);
        Assert.Equal(ReconnectState.Cancelled, cancelled.State);
        Assert.Null(cancelled.ErrorCode);
    }

    [Fact]
    public void AuthorizedOperationCannotBeOverwrittenByLateRuntimeFailure()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var started = tracker.TryStart().Operation;
        Assert.True(tracker.SetWaitingForUser(started.Id,
            "https://github.com/login/device", "ABCD-EFGH"));
        Assert.True(tracker.TryTransition(started.Id, ReconnectState.GitHubAuthorized, null));
        Assert.True(tracker.TryTransition(started.Id, ReconnectState.RestartingRuntime, null));
        Assert.True(tracker.TryTransition(started.Id, ReconnectState.Validating, null));
        Assert.True(tracker.TryTransition(started.Id, ReconnectState.SynchronizingModels, null));
        Assert.True(tracker.TryTransition(started.Id, ReconnectState.Authorized, null));

        Assert.False(tracker.TryTransition(started.Id, ReconnectState.RuntimeRestartFailed, "RuntimeExited"));

        var terminal = tracker.Get(started.Id);
        Assert.NotNull(terminal);
        Assert.Equal(ReconnectState.Authorized, terminal.State);
        Assert.Null(terminal.ErrorCode);
    }

    [Theory]
    [InlineData(ReconnectState.LoginFailed, "CopilotLoginFailed")]
    [InlineData(ReconnectState.Expired, "DeviceCodeExpired")]
    public void TerminalStatesAreStoredWithoutCredentialMaterial(
        ReconnectState state,
        string? errorCode)
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var started = tracker.TryStart().Operation;
        Assert.True(tracker.SetWaitingForUser(started.Id,
            "https://github.com/login/device", "ABCD-EFGH"));

        Assert.True(tracker.TryTransition(started.Id, state, errorCode));
        var terminal = tracker.Get(started.Id);

        Assert.NotNull(terminal);
        Assert.Equal(state, terminal.State);
        Assert.Equal(errorCode, terminal.ErrorCode);
        Assert.Null(terminal.UserCode);
        Assert.Null(terminal.VerificationUri);
    }

    [Fact]
    public async Task SuccessfulDeviceLoginRestartsRuntimeOnceBeforeValidation()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: false,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true),
            holdRestart: true);
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var started = await supervisor.StartReconnectAsync(CancellationToken.None);
        await runtime.RestartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ReconnectState.RestartingRuntime, supervisor.Get(started.Operation.Id)!.State);
        Assert.False(runtime.IsHeadlessReady);
        runtime.AllowRestart.TrySetResult();
        var operation = await WaitForStateAsync(supervisor, started.Operation.Id, ReconnectState.Validating);

        Assert.Equal(1, runtime.LoginCount);
        Assert.Equal(1, runtime.RestartCount);
        Assert.Equal("/var/lib/copilot", runtime.CopilotHome);
        Assert.Equal(ReconnectState.Validating, operation.State);
    }

    [Fact]
    public async Task ExistingCredentialIsRecoveredWithoutStartingAnotherDeviceFlow()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(1, CredentialPresent: true, PlaintextStorageConfirmed: false));
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var started = await supervisor.StartReconnectAsync(CancellationToken.None);
        await runtime.RestartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operation = await WaitForStateAsync(supervisor, started.Operation.Id, ReconnectState.Validating);

        Assert.Equal(0, runtime.LoginCount);
        Assert.Equal(0, runtime.LogoutCount);
        Assert.Equal(1, runtime.RestartCount);
        Assert.Equal(ReconnectState.Validating, operation.State);
    }

    [Fact]
    public async Task RepeatedValidationNeverTurnsIntoImplicitDeviceFlow()
    {
        var clock = new MutableTimeProvider();
        var tracker = new ReconnectOperationTracker(clock);
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true));
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var first = await supervisor.StartReconnectAsync(CancellationToken.None);
        await WaitForStateAsync(supervisor, first.Operation.Id, ReconnectState.Validating);
        Assert.True(supervisor.TryTransition(
            first.Operation.Id, ReconnectState.ValidationFailed, "RequiresReconnect"));
        clock.Advance(TimeSpan.FromSeconds(2));

        var second = await supervisor.StartReconnectAsync(CancellationToken.None);
        await WaitForStateAsync(supervisor, second.Operation.Id, ReconnectState.Validating);

        Assert.Equal(0, runtime.LoginCount);
        Assert.Equal(0, runtime.LogoutCount);
        Assert.Equal(2, runtime.RestartCount);
    }

    [Fact]
    public async Task ForceReauthenticateStartsDeviceFlowWithoutStoppingRuntimeOrLogout()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true));
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var started = await supervisor.StartReconnectAsync(
            ReconnectMode.ForceReauthenticate, confirmed: true, CancellationToken.None);
        var operation = await WaitForStateAsync(
            supervisor, started.Operation.Id, ReconnectState.Validating);

        Assert.Equal(0, runtime.StopHeadlessCount);
        Assert.Equal(0, runtime.LogoutCount);
        Assert.Equal(1, runtime.LoginCount);
        Assert.Equal(1, runtime.RestartCount);
        Assert.Equal(["login", "restart"], runtime.CallOrder);
        Assert.Equal("/var/lib/copilot", runtime.CopilotHome);
    }

    [Fact]
    public void CurrentOperationCanBeRecoveredAfterPageReloadWithoutStartingAnotherOperation()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var started = tracker.TryStart().Operation;
        Assert.True(tracker.SetWaitingForUser(started.Id,
            "https://github.com/login/device", "ABCD-EFGH"));

        var current = tracker.GetCurrent();

        Assert.NotNull(current);
        Assert.Equal(started.Id, current.Id);
        Assert.Equal(ReconnectState.WaitingForUser, current.State);
    }

    [Fact]
    public void LogoutCommandPrefersDocumentedDirectCommandAndNeverUsesBareLogout()
    {
        var direct = CopilotLogoutCommand.Select(directAuthLogoutSupported: true);
        var interactive = CopilotLogoutCommand.Select(directAuthLogoutSupported: false);

        Assert.Equal(["auth", "logout"], direct.Arguments);
        Assert.Equal(["-i", "/logout", "--no-color"], interactive.Arguments);
        Assert.False(direct.Arguments.SequenceEqual(["logout"]));
        Assert.False(interactive.Arguments.SequenceEqual(["logout"]));
    }

    [Fact]
    public void LogoutOutputIsReducedToSafeTypedSignalsWithoutRetainingRawOutput()
    {
        var observation = new CopilotLogoutOutputObservation();

        observation.Observe("Logged out successfully credential=secret-must-not-escape".AsSpan());

        Assert.True(observation.LogoutSucceeded);
        Assert.False(observation.UnsupportedCommand);
        Assert.DoesNotContain(observation.GetType().GetProperties(),
            property => property.PropertyType == typeof(string));
    }

    [Theory]
    [InlineData("Already logged out", CopilotLogoutStatus.AlreadyUnauthenticated)]
    [InlineData("Invalid command format", CopilotLogoutStatus.UnsupportedCommand)]
    public void LogoutOutputClassifiesSupportedSafeTerminalConditions(
        string output,
        CopilotLogoutStatus expected)
    {
        var observation = new CopilotLogoutOutputObservation();
        observation.Observe(output.AsSpan());

        var actual = observation.AlreadyUnauthenticated
            ? CopilotLogoutStatus.AlreadyUnauthenticated
            : observation.UnsupportedCommand
                ? CopilotLogoutStatus.UnsupportedCommand
                : CopilotLogoutStatus.Failed;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExplicitLogoutFailureRestoresRuntimeAndDoesNotStartDeviceFlow()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true))
        {
            LogoutResult = new(CopilotLogoutStatus.Failed, 1, CredentialPresent: true),
        };
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var result = await supervisor.EndLocalSessionAsync(CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Equal("CopilotLogoutFailed", result.ErrorCode);
        Assert.Equal(1, runtime.LogoutCount);
        Assert.Equal(0, runtime.LoginCount);
        Assert.Equal(1, runtime.RestartCount);
    }

    [Fact]
    public async Task ExplicitLogoutAcceptsAlreadyUnauthenticatedWithoutLogin()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: false,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true))
        {
            LogoutResult = new(CopilotLogoutStatus.AlreadyUnauthenticated, 0,
                CredentialPresent: false),
        };
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var result = await supervisor.EndLocalSessionAsync(CancellationToken.None);

        Assert.Equal("AlreadyUnauthenticated", result.Status);
        Assert.Equal(["stop", "logout"], runtime.CallOrder);
    }

    [Fact]
    public async Task LogoutTimeoutDoesNotStartLogin()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true))
        {
            LogoutResult = new(CopilotLogoutStatus.TimedOut, null, CredentialPresent: true),
        };
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var result = await supervisor.EndLocalSessionAsync(CancellationToken.None);

        Assert.Equal("CopilotLogoutTimeout", result.ErrorCode);
        Assert.Equal(0, runtime.LoginCount);
        Assert.Equal(["stop", "logout", "restart"], runtime.CallOrder);
    }

    [Fact]
    public async Task RuntimeIsFullyStoppedBeforeLogoutBegins()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true),
            holdStop: true);
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var logout = supervisor.EndLocalSessionAsync(CancellationToken.None);
        await runtime.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, runtime.LogoutCount);
        runtime.AllowStop.TrySetResult();
        await runtime.LogoutObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["stop", "logout"], runtime.CallOrder.Take(2));
        await logout;
    }

    [Fact]
    public async Task ParallelForceReauthenticationReturnsTheSameActiveOperation()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true),
            holdLogin: true);
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var first = await supervisor.StartReconnectAsync(
            ReconnectMode.ForceReauthenticate, confirmed: true, CancellationToken.None);
        await runtime.LoginObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = await supervisor.StartReconnectAsync(
            ReconnectMode.ForceReauthenticate, confirmed: true, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Operation.Id, duplicate.Operation.Id);
        Assert.Equal(0, runtime.LogoutCount);
        Assert.Equal(1, runtime.LoginCount);
        runtime.AllowLogin.TrySetResult();
    }

    [Fact]
    public async Task CancelWhileWaitingTransitionsOnceToCancelledAndDoesNotRestartRuntime()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true),
            holdLogin: true);
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var started = await supervisor.StartReconnectAsync(
            ReconnectMode.ForceReauthenticate, confirmed: true, CancellationToken.None);
        await runtime.LoginObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancel = supervisor.CancelAsync(started.Operation.Id);
        runtime.AllowLogin.TrySetResult();

        Assert.True(await cancel);
        var operation = await WaitForStateAsync(
            supervisor, started.Operation.Id, ReconnectState.Cancelled);
        Assert.Equal(ReconnectState.Cancelled, operation.State);
        Assert.Equal(0, runtime.RestartCount);
    }

    [Fact]
    public async Task CancelAfterGitHubApprovalCannotOverwriteTheCommittedLoginPhase()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(0, CredentialPresent: true, PlaintextStorageConfirmed: true));
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);
        var started = tracker.TryStart(ReconnectState.StartingLogin).Operation;
        Assert.True(tracker.SetWaitingForUser(started.Id,
            "https://github.com/login/device", "ABCD-EFGH"));
        Assert.True(tracker.TryTransition(started.Id, ReconnectState.GitHubAuthorized, null));

        Assert.True(await supervisor.CancelAsync(started.Id));

        Assert.Equal(ReconnectState.GitHubAuthorized, tracker.Get(started.Id)!.State);
    }

    [Fact]
    public async Task NonZeroLoginExitIsAuthorizationFailedAndDoesNotRestartRuntime()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: false,
            new CopilotLoginResult(1, CredentialPresent: false, PlaintextStorageConfirmed: false));
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var started = await supervisor.StartReconnectAsync(CancellationToken.None);
        var operation = await WaitForStateAsync(
            supervisor, started.Operation.Id, ReconnectState.LoginFailed);

        Assert.Equal("CopilotLoginFailed", operation.ErrorCode);
        Assert.Equal(1, operation.ProcessExitCode);
        Assert.Equal("LoginProcessExited", operation.FailureReason);
        Assert.Equal(1, runtime.LoginCount);
        Assert.Equal(0, runtime.RestartCount);
    }

    [Fact]
    public async Task FailedForceReauthenticationLeavesExistingRuntimeAndCredentialUntouched()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var runtime = new FakeRuntimeHost(hasStoredCredential: true,
            new CopilotLoginResult(1, CredentialPresent: true, PlaintextStorageConfirmed: false));
        using var supervisor = new CopilotSupervisor(
            NullLogger<CopilotSupervisor>.Instance, tracker, runtime);

        var started = await supervisor.StartReconnectAsync(
            ReconnectMode.ForceReauthenticate, confirmed: true, CancellationToken.None);
        var operation = await WaitForStateAsync(
            supervisor, started.Operation.Id, ReconnectState.LoginFailed);

        Assert.Equal("CopilotLoginFailed", operation.ErrorCode);
        Assert.Equal(1, runtime.LoginCount);
        Assert.Equal(0, runtime.StopHeadlessCount);
        Assert.Equal(0, runtime.LogoutCount);
        Assert.Equal(0, runtime.RestartCount);
        Assert.True(runtime.HasStoredCredential);
        Assert.Equal(["login"], runtime.CallOrder);
    }

    [Fact]
    public void FullSuccessfulFlowCannotBeChangedByLateOldRuntimeExit()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var id = tracker.TryStart().Operation.Id;
        Assert.True(tracker.TryTransition(id, ReconnectState.GitHubAuthorized, null));
        Assert.True(tracker.TryTransition(id, ReconnectState.RestartingRuntime, null));
        Assert.True(tracker.TryTransition(id, ReconnectState.Validating, null));
        Assert.True(tracker.TryTransition(id, ReconnectState.SynchronizingModels, null));
        Assert.True(tracker.TryTransition(id, ReconnectState.Authorized, null));

        Assert.False(tracker.TryTransition(id, ReconnectState.RuntimeRestartFailed, "LateRuntimeExit"));
        Assert.Equal(ReconnectState.Authorized, tracker.Get(id)!.State);
    }

    [Fact]
    public void CatalogFailureIsDistinctFromAuthorizationFailure()
    {
        var tracker = new ReconnectOperationTracker(new MutableTimeProvider());
        var id = tracker.TryStart().Operation.Id;
        Assert.True(tracker.TryTransition(id, ReconnectState.GitHubAuthorized, null));
        Assert.True(tracker.TryTransition(id, ReconnectState.RestartingRuntime, null));
        Assert.True(tracker.TryTransition(id, ReconnectState.Validating, null));
        Assert.True(tracker.TryTransition(id, ReconnectState.ModelSyncFailed,
            "ModelCatalogUnavailable"));

        var operation = tracker.Get(id)!;
        Assert.Equal(ReconnectState.ModelSyncFailed, operation.State);
        Assert.Equal("ModelCatalogUnavailable", operation.ErrorCode);
    }

    [Fact]
    public void PlaintextPromptFromStderrWithoutNewlineIsRecognizedAcrossChunksWithoutRawOutput()
    {
        var parser = new CopilotLoginOutputParser();

        var first = parser.Observe("No credential helper. Save the token in a ".AsSpan());
        var second = parser.Observe("plain text file?".AsSpan());

        Assert.False(first.PlaintextStoragePrompt);
        Assert.True(second.PlaintextStoragePrompt);
        Assert.Null(first.DevicePrompt);
        Assert.Null(second.DevicePrompt);
    }

    [Fact]
    public void FormalStateMachineRejectsLateEventsAndLimitsCancellationToPreApproval()
    {
        Assert.True(LocalCopilotOperationStateMachine.CanTransition(
            ReconnectState.StartingLogin, ReconnectState.WaitingForUser));
        Assert.True(LocalCopilotOperationStateMachine.CanCancel(ReconnectState.WaitingForUser));
        Assert.False(LocalCopilotOperationStateMachine.CanCancel(ReconnectState.GitHubAuthorized));
        Assert.True(LocalCopilotOperationStateMachine.IsTerminal(ReconnectState.Authorized));
        Assert.False(LocalCopilotOperationStateMachine.CanTransition(
            ReconnectState.Authorized, ReconnectState.RuntimeRestartFailed));
        Assert.False(LocalCopilotOperationStateMachine.ExposesDeviceCode(ReconnectState.Validating));
    }

    [Fact]
    public async Task ProductionRuntimeUsesDeterministicLoginProcessRunnerWithoutExposingOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"local-copilot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), "{}");
        try
        {
            var runner = new ScriptedLoginProcessRunner(
                new CopilotLoginProcessResult(0, PlaintextStorageConfirmed: true));
            using var runtime = new CopilotCliRuntimeHost(runner, directory);
            CopilotDevicePrompt? prompt = null;

            var result = await runtime.LoginAsync(value => prompt = value, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.CredentialPresent);
            Assert.True(result.PlaintextStorageConfirmed);
            Assert.Equal("https://github.com/login/device", prompt?.VerificationUri);
            Assert.Equal(1, runner.CallCount);
            Assert.False(runner.StopCalled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ReconnectOperationDto> WaitForStateAsync(
        CopilotSupervisor supervisor,
        Guid id,
        ReconnectState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var operation = supervisor.Get(id)!;
            if (operation.State == expected) return operation;
            await Task.Yield();
        }
    }

    private sealed class FakeRuntimeHost(
        bool hasStoredCredential,
        CopilotLoginResult loginResult,
        bool holdRestart = false,
        bool holdLogout = false,
        bool holdStop = false,
        bool holdLogin = false) : ICopilotCliRuntimeHost
    {
        public TaskCompletionSource RestartObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRestart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LogoutObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowLogout { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoginObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowLogin { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> CallOrder { get; } = [];
        public bool IsHeadlessReady { get; private set; }
        public bool HasStoredCredential { get; } = hasStoredCredential;
        public string CopilotHome => "/var/lib/copilot";
        public int LoginCount { get; private set; }
        public int StopHeadlessCount { get; private set; }
        public int LogoutCount { get; private set; }
        public int RestartCount { get; private set; }
        public CopilotLogoutResult LogoutResult { get; init; } =
            new(CopilotLogoutStatus.Succeeded, 0, CredentialPresent: false);

        public async Task<CopilotLoginResult> LoginAsync(
            Action<CopilotDevicePrompt> onDevicePrompt,
            CancellationToken cancellationToken)
        {
            LoginCount++;
            CallOrder.Add("login");
            onDevicePrompt(new("https://github.com/login/device", "ABCD-EFGH"));
            LoginObserved.TrySetResult();
            if (holdLogin) await AllowLogin.Task.WaitAsync(cancellationToken);
            return loginResult;
        }

        public async Task StopHeadlessAsync(CancellationToken cancellationToken)
        {
            StopHeadlessCount++;
            CallOrder.Add("stop");
            IsHeadlessReady = false;
            StopObserved.TrySetResult();
            if (holdStop) await AllowStop.Task.WaitAsync(cancellationToken);
        }

        public async Task RestartHeadlessAsync(CancellationToken cancellationToken)
        {
            RestartCount++;
            CallOrder.Add("restart");
            RestartObserved.TrySetResult();
            if (holdRestart) await AllowRestart.Task.WaitAsync(cancellationToken);
            IsHeadlessReady = true;
        }

        public async Task<CopilotLogoutResult> LogoutAsync(CancellationToken cancellationToken)
        {
            LogoutCount++;
            CallOrder.Add("logout");
            IsHeadlessReady = false;
            LogoutObserved.TrySetResult();
            if (holdLogout) await AllowLogout.Task.WaitAsync(cancellationToken);
            return LogoutResult;
        }

        public void StopLogin() { }
        public void Dispose() { }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan value) => utcNow = utcNow.Add(value);
    }

    private sealed class ScriptedLoginProcessRunner(CopilotLoginProcessResult result)
        : ICopilotLoginProcessRunner
    {
        public int CallCount { get; private set; }
        public bool StopCalled { get; private set; }

        public Task<CopilotLoginProcessResult> RunAsync(
            string copilotHome,
            Action<CopilotDevicePrompt> onDevicePrompt,
            CancellationToken cancellationToken)
        {
            CallCount++;
            onDevicePrompt(new("https://github.com/login/device", "ABCD-EFGH"));
            return Task.FromResult(result);
        }

        public void StopProcess() => StopCalled = true;
    }
}
