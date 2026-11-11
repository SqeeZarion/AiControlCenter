using System.Collections.Concurrent;
using AiControlCenter.Integrations.Infrastructure.Providers;

namespace AiControlCenter.Integrations.Api;

public sealed partial class LocalCopilotReconnectCoordinator(
    IServiceScopeFactory scopeFactory)
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> completions = new();

    public async Task<LocalCopilotReconnectDto> CompleteAsync(
        Guid owner,
        LocalCopilotReconnectDto operation,
        CancellationToken requestToken)
    {
        if (operation.State != LocalCopilotReconnectState.Validating) return operation;
        var completion = completions.GetOrAdd(operation.OperationId,
            _ => new Lazy<Task>(() => CompleteCoreAsync(owner, operation.OperationId),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var task = completion.Value;
        _ = RemoveWhenCompleteAsync(operation.OperationId, completion, task);
        if (!task.IsCompleted) return operation;
        await task;

        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LocalCopilotReconnectClient>()
            .GetAsync(operation.OperationId, requestToken);
    }

    private async Task RemoveWhenCompleteAsync(Guid operationId, Lazy<Task> completion, Task task)
    {
        try { await task; }
        finally { completions.TryRemove(new(operationId, completion)); }
    }

    private async Task CompleteCoreAsync(Guid owner, Guid operationId)
    {
        using var timeout = new CancellationTokenSource(CompletionTimeout);
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LocalCopilotReconnectWorkflow>()
            .CompleteAsync(owner, operationId, timeout.Token);
    }
}
