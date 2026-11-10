using AiControlCenter.ControlPlane.Api.Grpc;
using AiControlCenter.ControlPlane.Application;
using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class AgentModelRouteValidatorTests
{
    private const string InternalKey = "route-validator-test-key-32-characters";

    [Fact]
    public async Task ProductionValidatorSendsOwnerAndExplicitConnectionToIntegrationsGrpc()
    {
        var owner = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var routes = $$"""[{"provider":"LocalCopilotCli","connectionId":"{{connection}}","modelId":"Auto","priority":1}]""";
        var invoker = new RouteValidationCallInvoker(allAvailable: true);
        var validator = Create(invoker);

        await validator.ValidateAsync(owner, routes, CancellationToken.None);

        Assert.NotNull(invoker.Request);
        Assert.Equal(owner.ToString(), invoker.Request.OwnerUserId);
        Assert.Equal(routes, invoker.Request.LogicalRoutesJson);
        Assert.Equal(InternalKey, invoker.ApiKey);
    }

    [Fact]
    public async Task ForeignOrUnavailableConnectionReturnsTypedFieldValidation()
    {
        var validator = Create(new RouteValidationCallInvoker(allAvailable: false));

        var exception = await Assert.ThrowsAsync<AgentModelRouteValidationException>(() =>
            validator.ValidateAsync(Guid.NewGuid(), "[]", CancellationToken.None));

        Assert.Equal("AgentRouteConnectionUnavailable", exception.Code);
        Assert.Equal("modelRoutesJson", exception.Field);
        Assert.DoesNotContain("credential", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidGrpcRouteReturnsTypedSafeValidation()
    {
        var validator = Create(new RouteValidationCallInvoker(
            allAvailable: false, statusCode: StatusCode.InvalidArgument));

        var exception = await Assert.ThrowsAsync<AgentModelRouteValidationException>(() =>
            validator.ValidateAsync(Guid.NewGuid(), "not-json", CancellationToken.None));

        Assert.Equal("AgentRouteInvalid", exception.Code);
        Assert.Equal("modelRoutesJson", exception.Field);
        Assert.DoesNotContain("not-json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableIntegrationsReturnsTypedServiceUnavailableValidation()
    {
        var validator = Create(new RouteValidationCallInvoker(
            allAvailable: false, statusCode: StatusCode.Unavailable));

        var exception = await Assert.ThrowsAsync<AgentModelRouteValidationException>(() =>
            validator.ValidateAsync(Guid.NewGuid(), "[]", CancellationToken.None));

        Assert.Equal("AgentRouteValidationUnavailable", exception.Code);
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("modelRoutesJson", exception.Field);
    }

    private static AgentModelRouteValidator Create(CallInvoker invoker)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkerGrpc:ApiKey"] = InternalKey,
            })
            .Build();
        return new(new AiProviderExecution.AiProviderExecutionClient(invoker), configuration);
    }

    private sealed class RouteValidationCallInvoker(
        bool allAvailable,
        StatusCode statusCode = StatusCode.OK) : CallInvoker
    {
        public ResolveAiProviderRoutesRequest? Request { get; private set; }
        public string? ApiKey { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            Request = request as ResolveAiProviderRoutesRequest;
            ApiKey = options.Headers?.GetValue("x-worker-api-key");
            if (statusCode != StatusCode.OK)
            {
                return new AsyncUnaryCall<TResponse>(
                    Task.FromException<TResponse>(new RpcException(new Status(statusCode, "safe"))),
                    Task.FromResult(new Metadata()),
                    () => new Status(statusCode, "safe"),
                    static () => [],
                    static () => { });
            }

            object reply = new ResolveAiProviderRoutesReply
            {
                ResolvedRoutesJson = "[]",
                AllAvailable = allAvailable,
            };
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)reply),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }
}
