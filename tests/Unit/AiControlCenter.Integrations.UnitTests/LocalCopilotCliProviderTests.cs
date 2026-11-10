using System.Net;
using System.Text;
using System.Text.Json;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Providers;
using AiControlCenter.LocalCopilot.Sidecar;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class LocalCopilotCliProviderTests
{
    private static readonly ProviderModelDto Model = new(
        "gpt-test", "GPT Test", null, true, false, null, null);

    [Fact]
    public async Task DisabledFeatureFailsClosedWithoutCallingRuntime()
    {
        var runtime = new FakeRuntime();
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor(enabled: false)));

        var result = await provider.ValidateConnectionAsync(null, CancellationToken.None);

        Assert.Equal(ProviderCredentialValidationKind.TemporarilyUnavailable, result.Kind);
        Assert.Equal(0, runtime.InspectCount);
    }

    [Fact]
    public async Task ValidationUsesAuthenticatedExternalRuntimeWithoutCredentialPersistence()
    {
        var runtime = new FakeRuntime
        {
            Inspection = new([Model], "local-owner", true),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));

        var result = await provider.ValidateConnectionAsync(null, CancellationToken.None);
        var connection = ProviderConnection.Create(Guid.NewGuid(),
            AiProviderKind.GitHubCopilotLocalCli, "Local Copilot CLI", null, null,
            DateTimeOffset.UtcNow);

        Assert.Equal(ProviderCredentialValidationKind.Valid, result.Kind);
        Assert.Equal("local-owner", result.ExternalAccountLogin);
        Assert.Null(connection.ProtectedCredential);
        Assert.Null(connection.CredentialFingerprint);
    }

    [Fact]
    public async Task RuntimeCanRecoverOnNextControlledValidationWithoutRetryLoop()
    {
        var runtime = new FakeRuntime
        {
            FailInspections = 1,
            Inspection = new([Model], "local-owner", true),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));

        var unavailable = await provider.ValidateConnectionAsync(null, CancellationToken.None);
        var recovered = await provider.ValidateConnectionAsync(null, CancellationToken.None);

        Assert.Equal(ProviderCredentialValidationKind.TemporarilyUnavailable, unavailable.Kind);
        Assert.Equal(ProviderCredentialValidationKind.Valid, recovered.Kind);
        Assert.Equal(2, runtime.InspectCount);
    }

    [Fact]
    public async Task AtomicRuntimeSessionReportsExpiredAuthenticationWithoutGeneration()
    {
        var runtime = new FakeRuntime
        {
            Inspection = new([Model], "local-owner", false),
            ExecuteException = new UnauthorizedAccessException(),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));
        var request = new AiExecutionRequest(
            Guid.NewGuid(), Guid.NewGuid(), "gpt-test", "system", "input", [], null, "idempotency");

        var result = await provider.ExecuteAsync(request, null, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Authentication, result.FailureKind);
        Assert.Equal("connection_requires_reconnect", result.ErrorCode);
        Assert.Equal(0, runtime.InspectCount);
        Assert.Equal(1, runtime.ExecuteCount);
        Assert.Equal(0, result.OutboundRequestCount);
    }

    [Fact]
    public async Task GenerationUsesOneAtomicRuntimeSessionAfterCatalogPreflight()
    {
        var runtime = new FakeRuntime
        {
            Inspection = new([Model], "local-owner", true),
            FailInspections = 1,
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));
        var request = new AiExecutionRequest(
            Guid.NewGuid(), Guid.NewGuid(), "auto", "system", "input", [], null, "idempotency");

        var result = await provider.ExecuteAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("ok", result.Output);
        Assert.Equal(0, runtime.InspectCount);
        Assert.Equal(1, runtime.ExecuteCount);
        Assert.Equal(1, result.OutboundRequestCount);
    }

    [Fact]
    public async Task AutoRoutingDoesNotClaimTheAutoSentinelAsTheActualModel()
    {
        var runtime = new FakeRuntime
        {
            Inspection = new([new("auto", "Auto", null, true, false, null, null)], "local-owner", true),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));
        var request = new AiExecutionRequest(
            Guid.NewGuid(), Guid.NewGuid(), "auto", "system", "input", [], null, "idempotency");

        var result = await provider.ExecuteAsync(request, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ActualModel);
    }

    [Fact]
    public async Task SessionCreateFailureIsTypedAndProvenPredispatch()
    {
        var runtime = new FakeRuntime
        {
            ExecuteException = LocalCopilotRuntimeException.Predispatch(
                "SessionCreate", "session_create_failed", "InvalidOperationException"),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));

        var result = await provider.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Configuration, result.FailureKind);
        Assert.Equal("SessionCreate", result.Operation);
        Assert.Equal("false", result.GenerationDispatched);
        Assert.Equal(0, result.OutboundRequestCount);
    }

    [Fact]
    public async Task FailureAfterGenerationDispatchIsAmbiguousAndNeverRetryable()
    {
        var runtime = new FakeRuntime
        {
            ExecuteException = LocalCopilotRuntimeException.Ambiguous(
                "GenerationDispatch", "generation_outcome_ambiguous", "TimeoutException"),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));

        var result = await provider.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.AmbiguousTimeout, result.FailureKind);
        Assert.Equal("GenerationDispatch", result.Operation);
        Assert.Equal("unknown", result.GenerationDispatched);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
    }

    [Fact]
    public async Task DeterministicDispatchRejectionDoesNotBecomeAmbiguousTimeout()
    {
        var runtime = new FakeRuntime
        {
            ExecuteException = LocalCopilotRuntimeException.Predispatch(
                "GenerationDispatch", "generation_model_rejected", nameof(RemoteRpcException)),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));

        var result = await provider.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.ModelUnavailable, result.FailureKind);
        Assert.Equal("generation_model_rejected", result.ErrorCode);
        Assert.Equal("false", result.GenerationDispatched);
        Assert.Equal(0, result.OutboundRequestCount);
        Assert.Null(result.ActualModel);
    }

    [Fact]
    public async Task ProductionDispatchInvokesTheSdkContractExactlyOnce()
    {
        var session = new FakeSdkSession();
        var runtime = new OfficialLocalCopilotRuntime(
            Options.Create(OptionsFor()),
            NullLogger<OfficialLocalCopilotRuntime>.Instance);
        var diagnostics = new List<ProviderOperationDiagnostic>();

        var response = await runtime.DispatchGenerationAsync(
            session,
            new MessageOptions { Prompt = "safe-test" },
            diagnostics,
            CancellationToken.None);

        Assert.Null(response);
        Assert.Equal(1, session.CallCount);
        Assert.Contains(diagnostics, item => item.Marker == "DispatchCallStarted");
        Assert.Contains(diagnostics, item => item.Marker == "TransportAccepted");
        Assert.Contains(diagnostics, item => item.Marker == "GenerationResponseReceived");
    }

    [Fact]
    public void DeterministicRpcRejectionIsPredispatchNotAmbiguous()
    {
        using var data = JsonDocument.Parse("""{"code":"model_invalid"}""");
        var remote = new RemoteRpcException("untrusted", -32000, data.RootElement, null);

        var failure = OfficialLocalCopilotRuntime.ClassifyDispatchFailure(
            new IOException("wrapper", remote));

        Assert.Equal("generation_model_rejected", failure.ErrorCode);
        Assert.Equal("DispatchFailedBeforeAcceptance", failure.Marker);
        Assert.Equal("false", failure.GenerationDispatched);
        Assert.Equal(nameof(RemoteRpcException), failure.SafeExceptionType);
    }

    [Fact]
    public void TimeoutWithoutAcceptanceEvidenceRemainsAmbiguous()
    {
        var failure = OfficialLocalCopilotRuntime.ClassifyDispatchFailure(new TimeoutException());

        Assert.Equal("generation_outcome_ambiguous", failure.ErrorCode);
        Assert.Equal("OutcomeUnknownAfterDispatch", failure.Marker);
        Assert.Equal("unknown", failure.GenerationDispatched);
    }

    [Fact]
    public void RpcFailureWithCompletionEvidenceIsAcceptedAndNeverSafeToRepeat()
    {
        using var data = JsonDocument.Parse(
            """{"code":"policy_blocked","completionEventId":"event-1","requestId":"request-1"}""");
        var remote = new RemoteRpcException("untrusted", -32000, data.RootElement, null);

        var failure = OfficialLocalCopilotRuntime.ClassifyDispatchFailure(
            new IOException("wrapper", remote));

        Assert.Equal("generation_policy_rejected", failure.ErrorCode);
        Assert.Equal("TransportRejectedAfterAcceptance", failure.Marker);
        Assert.Equal("true", failure.GenerationDispatched);
        Assert.Equal("request-1", failure.ProviderRequestId);
    }

    [Fact]
    public void RuntimeBinderContractFailureIsProvenPredispatch()
    {
        var failure = OfficialLocalCopilotRuntime.ClassifyDispatchFailure(
            new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException());

        Assert.Equal("dispatch_contract_invalid", failure.ErrorCode);
        Assert.Equal("DispatchFailedBeforeAcceptance", failure.Marker);
        Assert.Equal("false", failure.GenerationDispatched);
    }

    [Fact]
    public void AutoWithOneConcreteCatalogModelResolvesBeforeSessionCreation()
    {
        var diagnostics = new List<ProviderOperationDiagnostic>();
        var resolution = OfficialLocalCopilotRuntime.ResolveModel("Auto", [Model], diagnostics);

        Assert.Equal("gpt-test", resolution.SessionModel);
        Assert.Equal("gpt-test", resolution.ConfirmedActualModel);
        Assert.Collection(diagnostics,
            started =>
            {
                Assert.Equal("ModelResolution", started.Operation);
                Assert.Equal("Started", started.Marker);
                Assert.Equal("false", started.GenerationDispatched);
            },
            completed =>
            {
                Assert.Equal("ModelResolution", completed.Operation);
                Assert.Equal("Completed", completed.Marker);
                Assert.Equal("false", completed.GenerationDispatched);
            });
    }

    [Fact]
    public void AutoSentinelUsesTheOfficialSdkAutoModelWithoutClaimingAnActualModel()
    {
        var resolution = OfficialLocalCopilotRuntime.ResolveModel("Auto",
            [new("auto", "Auto", null, true, false, null, null)]);

        Assert.Equal("auto", resolution.SessionModel);
        Assert.Null(resolution.ConfirmedActualModel);
    }

    [Fact]
    public void ProductionSessionConfigUsesOnlySupportedHeadlessOptions()
    {
        var config = OfficialLocalCopilotRuntime.BuildSessionConfig("auto", [], []);

        Assert.Equal("auto", config.Model);
        Assert.False(config.Streaming);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<AIFunction>>(config.Tools));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<string>>(config.AvailableTools));
        Assert.False(config.EnableSessionStore);
        Assert.Equal("/tmp/aicontrolcenter-session", config.WorkingDirectory);
        Assert.Equal(WorkingDirectoryTrust.Session, config.TrustWorkingDirectory);
        Assert.Null(config.SystemMessage);
        Assert.Null(config.SessionId);
    }

    [Fact]
    public void SessionConfigurationFingerprintContainsOnlySafeContractFacts()
    {
        const string secret = "never-include-this-connection-token";
        var config = OfficialLocalCopilotRuntime.BuildSessionConfig("auto", [], []);

        var fingerprint = OfficialLocalCopilotRuntime.SessionConfigurationFingerprint(config, secret);

        Assert.Contains("model=auto", fingerprint, StringComparison.Ordinal);
        Assert.Contains("systemMessage=omitted", fingerprint, StringComparison.Ordinal);
        Assert.Contains("tools=0", fingerprint, StringComparison.Ordinal);
        Assert.Contains("streaming=omitted", fingerprint, StringComparison.Ordinal);
        Assert.Contains("clientMode=empty", fingerprint, StringComparison.Ordinal);
        Assert.Contains("runtimeConnection=uri", fingerprint, StringComparison.Ordinal);
        Assert.Contains("connectionToken=present", fingerprint, StringComparison.Ordinal);
        Assert.Contains("trustWorkingDirectory=session", fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/aicontrolcenter-session", fingerprint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-32601, "session_runtime_incompatible")]
    [InlineData(-32602, "session_option_unsupported")]
    [InlineData(-32000, "session_create_protocol_error")]
    public void RemoteSessionCreateFailuresUseMachineReadableRpcCodes(
        int rpcCode,
        string expected)
    {
        var remote = new RemoteRpcException("untrusted remote text", rpcCode, null, null);
        var exception = new IOException("SDK communication wrapper", remote);

        Assert.Equal(expected, OfficialLocalCopilotRuntime.SessionCreateResultCode(exception));
    }

    [Theory]
    [InlineData("model_required", "session_model_required")]
    [InlineData("model_invalid", "session_model_invalid")]
    [InlineData("unsupported_option", "session_option_unsupported")]
    [InlineData("protocol_version_mismatch", "session_runtime_incompatible")]
    [InlineData("connection_rejected", "session_connection_rejected")]
    public void AllowlistedRemoteCategoriesAreTypedWithoutUsingRemoteMessages(
        string category,
        string expected)
    {
        using var data = JsonDocument.Parse($$"""{"code":"{{category}}","detail":"secret raw detail"}""");
        var remote = new RemoteRpcException("untrusted remote text", -32000,
            data.RootElement, null);

        var actual = OfficialLocalCopilotRuntime.SessionCreateResultCode(
            new IOException("SDK communication wrapper", remote));

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("secret", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("untrusted", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSessionWorkspaceIsRecreatedAfterTmpfsMount()
    {
        var path = Path.Combine(Path.GetTempPath(), $"copilot-session-{Guid.NewGuid():N}");
        try
        {
            Assert.False(Directory.Exists(path));

            CopilotCliRuntimeHost.EnsureSessionWorkingDirectory(path);

            Assert.True(Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void UnknownExplicitModelFailsAtModelResolutionBeforeSessionCreation()
    {
        var exception = Assert.Throws<LocalCopilotRuntimeException>(() =>
            OfficialLocalCopilotRuntime.ResolveModel("unknown-model", [Model]));

        Assert.Equal("ModelResolution", exception.Operation);
        Assert.Equal("model_not_available", exception.ErrorCode);
        Assert.Equal("false", exception.GenerationDispatched);
    }

    [Fact]
    public async Task ProviderResponseRecordsOnlyTheConfirmedActualModel()
    {
        var runtime = new FakeRuntime
        {
            Result = new("ok", [], "gpt-5.4", "request-safe"),
        };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));

        var result = await provider.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("gpt-5.4", result.ActualModel);
        Assert.Equal("request-safe", result.ProviderRequestId);
    }

    [Fact]
    public async Task UnstagedGenericRuntimeFailureIsAmbiguousNotRetryable()
    {
        var runtime = new FakeRuntime { ExecuteException = new InvalidOperationException() };
        var provider = new GitHubCopilotLocalCliProvider(runtime, Options.Create(OptionsFor()));

        var result = await provider.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.AmbiguousTimeout, result.FailureKind);
        Assert.Equal("unknown", result.GenerationDispatched);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
    }

    [Fact]
    public async Task OwnerBindingRejectsEveryOtherPlatformUser()
    {
        var owner = Guid.NewGuid();
        var runtime = new FakeRuntime { Inspection = new([Model], "local-owner", true) };
        var access = new LocalCopilotCliAccess(Options.Create(OptionsFor(owner: owner)), runtime);

        Assert.True((await access.StatusAsync(owner, CancellationToken.None)).AvailableForCurrentUser);
        Assert.False((await access.StatusAsync(Guid.NewGuid(), CancellationToken.None)).AvailableForCurrentUser);
        Assert.Throws<LocalCopilotCliAccessDeniedException>(() => access.EnsureAllowed(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("Production", "local-copilot-cli:4321")]
    [InlineData("Development", "169.254.169.254:80")]
    [InlineData("Development", "host.docker.internal:4321")]
    public void StartupValidationRejectsNonDevelopmentAndNonAllowlistedEndpoints(
        string environmentName,
        string endpoint)
    {
        var validator = new LocalCopilotCliOptionsValidator(new FakeEnvironment(environmentName));
        var result = validator.Validate(null, OptionsFor(endpoint: endpoint));

        Assert.True(result.Failed);
    }

    [Fact]
    public void ServerCapabilitiesAndDirectGuardsFailClosed()
    {
        var access = new ProviderConnectionFeatureAccess(Options.Create(new ProviderConnectionFeatureOptions
        {
            GitHubCopilotOAuthEnabled = false,
            GitHubCopilotPatEnabled = false,
            LocalCopilotCliEnabled = true,
        }));

        var capabilities = access.Capabilities();

        Assert.False(capabilities.GitHubCopilotOAuthEnabled);
        Assert.False(capabilities.GitHubCopilotPatEnabled);
        Assert.True(capabilities.LocalCopilotCliEnabled);
        Assert.Equal("GitHubCopilotOAuthDisabled",
            Assert.Throws<ProviderFeatureDisabledException>(access.EnsureOAuthEnabled).Code);
        Assert.Equal("GitHubCopilotPatDisabled",
            Assert.Throws<ProviderFeatureDisabledException>(access.EnsurePatEnabled).Code);
        access.EnsureLocalCliEnabled();
        Assert.Equal("LocalCopilotReauthenticationUnsupported",
            Assert.Throws<ProviderFeatureDisabledException>(
                access.EnsureLocalCliReauthenticationEnabled).Code);
        Assert.Equal("LocalCopilotLogoutUnsupported",
            Assert.Throws<ProviderFeatureDisabledException>(access.EnsureLocalCliLogoutEnabled).Code);
    }

    [Fact]
    public async Task ReconnectClientReturnsOnlySanitizedDeviceFlowFields()
    {
        const string body = """
            {"id":"71b2e5d2-9d2f-4b03-b2fa-eaf7384ba21b","state":"WaitingForUser","verificationUri":"https://github.com/login/device","userCode":"ABCD-EFGH","expiresAt":"2026-10-30T12:15:00Z","errorCode":null}
            """;
        var handler = new StubHandler(body);
        var client = new LocalCopilotReconnectClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-copilot-cli:4322"),
        }, Options.Create(OptionsFor()));

        var owner = Guid.Parse("f4e6c1e5-a771-4ed2-8f7d-8b906c178a13");
        var result = await client.StartAsync(owner, "correlation-safe-1", CancellationToken.None);

        Assert.Equal(LocalCopilotReconnectState.WaitingForUser, result.State);
        Assert.Equal("ABCD-EFGH", result.UserCode);
        Assert.Equal("https://github.com/login/device", result.VerificationUri);
        Assert.Contains(owner.ToString(), handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correlation-safe-1", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal("test-connection-token-with-at-least-32-characters",
            handler.LastRequest?.Headers.GetValues("X-AiControlCenter-Local-Copilot-Token").Single());
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stdout", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("config", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconnectClientSendsOnlySafeStateAndErrorCode()
    {
        const string body = """
            {"id":"71b2e5d2-9d2f-4b03-b2fa-eaf7384ba21b","state":"ModelSyncFailed","verificationUri":null,"userCode":null,"expiresAt":"2026-10-30T12:15:00Z","errorCode":"ModelCatalogUnavailable"}
            """;
        var handler = new StubHandler(body);
        var client = new LocalCopilotReconnectClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-copilot-cli:4322"),
        }, Options.Create(OptionsFor()));

        var result = await client.TransitionAsync(
            Guid.Parse("71b2e5d2-9d2f-4b03-b2fa-eaf7384ba21b"),
            LocalCopilotReconnectState.ModelSyncFailed,
            "ModelCatalogUnavailable",
            CancellationToken.None);

        Assert.Equal(LocalCopilotReconnectState.ModelSyncFailed, result.State);
        Assert.Contains("ModelSyncFailed", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("ModelCatalogUnavailable", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("token", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stdout", handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForceReauthenticationRequiresAnExplicitConfirmationBody()
    {
        const string body = """
            {"id":"71b2e5d2-9d2f-4b03-b2fa-eaf7384ba21b","state":"StartingLogin","verificationUri":null,"userCode":null,"expiresAt":"2026-10-30T12:15:00Z","errorCode":null}
            """;
        var handler = new StubHandler(body);
        var client = new LocalCopilotReconnectClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-copilot-cli:4322"),
        }, Options.Create(OptionsFor()));

        var result = await client.StartForceReauthenticateAsync(
            true, Guid.Parse("f4e6c1e5-a771-4ed2-8f7d-8b906c178a13"),
            "correlation-safe-2", CancellationToken.None);

        Assert.Equal(LocalCopilotReconnectState.StartingLogin, result.State);
        Assert.Equal("/v1/reauthenticate", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Contains("\"confirmed\":true", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("correlation-safe-2", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("token", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "ConfigurationError")]
    [InlineData(HttpStatusCode.Forbidden, "InsufficientPermissions")]
    [InlineData(HttpStatusCode.NotFound, "SidecarOperationNotFound")]
    [InlineData(HttpStatusCode.TooManyRequests, "RateLimited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SidecarUnavailable")]
    public async Task ManagementClientMapsHttpFailuresToSafeTypedErrors(
        HttpStatusCode status,
        string expectedCode)
    {
        var handler = new StubHandler("{}", status);
        var client = new LocalCopilotReconnectClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-copilot-cli:4322"),
        }, Options.Create(OptionsFor()));

        var exception = await Assert.ThrowsAsync<LocalCopilotManagementException>(() =>
            client.GetAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal((int)status, exception.StatusCode);
        Assert.DoesNotContain("token", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulValidationSynchronizesModelsBeforeAuthorized()
    {
        var validation = new ProviderCredentialValidationResult(
            ProviderCredentialValidationKind.Valid,
            [new ProviderModelDto("model-1", "Model 1", null, true, false, null, null)],
            "local-cli:student-user", "student-user");
        var completion = new FakeCompletion(validation);
        var states = new FakeReconnectStateClient();
        var workflow = new LocalCopilotReconnectWorkflow(
            completion, states, NullLogger<LocalCopilotReconnectWorkflow>.Instance);

        await workflow.CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(1, completion.ValidationCount);
        Assert.Equal(1, completion.SynchronizationCount);
        Assert.Equal([
            LocalCopilotReconnectState.SynchronizingModels,
            LocalCopilotReconnectState.Authorized,
        ], states.Transitions);
    }

    [Fact]
    public async Task CatalogFailureAfterLoginIsModelSyncFailedNotAuthorizationFailed()
    {
        var completion = new FakeCompletion(null)
        {
            ValidationException = new ProviderCredentialRejectedException(
                ProviderCredentialValidationKind.ModelCatalogUnavailable.ToString(),
                "safe catalog failure"),
        };
        var states = new FakeReconnectStateClient();
        var workflow = new LocalCopilotReconnectWorkflow(
            completion, states, NullLogger<LocalCopilotReconnectWorkflow>.Instance);

        await workflow.CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([LocalCopilotReconnectState.ModelSyncFailed], states.Transitions);
        Assert.Equal([ProviderCredentialValidationKind.ModelCatalogUnavailable.ToString()],
            states.ErrorCodes);
        Assert.Equal(0, completion.SynchronizationCount);
    }

    [Fact]
    public async Task ValidationFailureDoesNotSynchronizeOrReplaceTheExistingConnection()
    {
        var completion = new FakeCompletion(null)
        {
            ValidationException = new ProviderCredentialRejectedException(
                ProviderCredentialValidationKind.InvalidCredential.ToString(),
                "safe validation failure"),
        };
        var states = new FakeReconnectStateClient();
        var workflow = new LocalCopilotReconnectWorkflow(
            completion, states, NullLogger<LocalCopilotReconnectWorkflow>.Instance);

        await workflow.CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([LocalCopilotReconnectState.ValidationFailed], states.Transitions);
        Assert.Equal([ProviderCredentialValidationKind.InvalidCredential.ToString()],
            states.ErrorCodes);
        Assert.Equal(0, completion.SynchronizationCount);
    }

    [Fact]
    public void EmptyModeToolFilterContainsOnlyExplicitCustomTools()
    {
        var tools = OfficialLocalCopilotRuntime.BuildAvailableTools([
            new ToolDefinition("text.length", "Counts text", "{}"),
        ]);

        Assert.Equal(["custom:text_length"], tools);
        Assert.DoesNotContain("builtin:*", tools);
        Assert.DoesNotContain("shell", tools);
        Assert.DoesNotContain("bash", tools);
    }

    [Fact]
    public void CanonicalToolNameCollisionsFailClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OfficialLocalCopilotRuntime.BuildAvailableTools([
                new ToolDefinition("text.length", "Counts text", "{}"),
                new ToolDefinition("text_length", "Also counts text", "{}"),
            ]));

        Assert.Contains("not unique", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalRuntimeClientUsesEmptyModeAndNoImplicitUserCredential()
    {
        var options = OfficialLocalCopilotRuntime.CreateClientOptions(OptionsFor());

        Assert.Equal(CopilotClientMode.Empty, options.Mode);
        Assert.Null(options.GitHubToken);
        Assert.Null(options.UseLoggedInUser);
        Assert.IsType<UriRuntimeConnection>(options.Connection);
    }

    private static LocalCopilotCliOptions OptionsFor(
        bool enabled = true,
        Guid? owner = null,
        string endpoint = "local-copilot-cli:4321") => new()
        {
            Enabled = enabled,
            Endpoint = endpoint,
            ConnectionToken = "test-connection-token-with-at-least-32-characters",
            OwnerUserId = owner ?? Guid.NewGuid(),
            RequestTimeoutSeconds = 30,
        };

    private static AiExecutionRequest Request() => new(
        Guid.NewGuid(), Guid.NewGuid(), "gpt-test", "system", "input", [], null, "idempotency");

    private sealed class FakeRuntime : ILocalCopilotRuntime
    {
        public LocalCopilotRuntimeInspection Inspection { get; init; } = new([], null, false);
        public int FailInspections { get; set; }
        public int InspectCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public Exception? ExecuteException { get; init; }
        public LocalCopilotRuntimeResult Result { get; init; } = new("ok", []);

        public Task<LocalCopilotRuntimeInspection> InspectAsync(CancellationToken cancellationToken)
        {
            InspectCount++;
            if (FailInspections-- > 0) throw new HttpRequestException("simulated runtime outage");
            return Task.FromResult(Inspection);
        }

        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(
            CancellationToken cancellationToken) => Task.FromResult(Inspection.Models);

        public Task<LocalCopilotRuntimeResult> ExecuteAsync(string model, string systemPrompt, string input,
            IReadOnlyCollection<ToolDefinition> tools,
            IReadOnlyCollection<ToolInvocationResult> priorResults,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            if (ExecuteException is not null) throw ExecuteException;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSdkSession : ILocalCopilotSdkSession
    {
        public int CallCount { get; private set; }

        public Task<AssistantMessageEvent?> SendAndWaitAsync(
            MessageOptions options,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<AssistantMessageEvent?>(null);
        }
    }

    private sealed class FakeCompletion(ProviderCredentialValidationResult? validation)
        : ILocalCopilotConnectionCompletion
    {
        public Exception? ValidationException { get; init; }
        public int ValidationCount { get; private set; }
        public int SynchronizationCount { get; private set; }

        public Task<ProviderCredentialValidationResult> ValidateAsync(CancellationToken token)
        {
            ValidationCount++;
            if (ValidationException is not null) throw ValidationException;
            return Task.FromResult(validation
                ?? throw new InvalidOperationException("Fake validation result is missing."));
        }

        public Task SynchronizeAsync(Guid owner, ProviderCredentialValidationResult result,
            CancellationToken token)
        {
            SynchronizationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReconnectStateClient : ILocalCopilotReconnectStateClient
    {
        public List<LocalCopilotReconnectState> Transitions { get; } = [];
        public List<string?> ErrorCodes { get; } = [];

        public Task<LocalCopilotReconnectDto> GetAsync(Guid operationId, CancellationToken token) =>
            Task.FromResult(Result(operationId,
                Transitions.LastOrDefault(LocalCopilotReconnectState.Validating)));

        public Task<LocalCopilotReconnectDto> TransitionAsync(Guid operationId,
            LocalCopilotReconnectState state, string? errorCode, CancellationToken token)
        {
            Transitions.Add(state);
            ErrorCodes.Add(errorCode);
            return Task.FromResult(Result(operationId, state, errorCode));
        }

        private static LocalCopilotReconnectDto Result(Guid id,
            LocalCopilotReconnectState state, string? errorCode = null) =>
            new(id, state, null, null, DateTimeOffset.UtcNow.AddMinutes(1), errorCode);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHandler(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.Accepted) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
