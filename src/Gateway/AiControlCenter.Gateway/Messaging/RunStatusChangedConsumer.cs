using AiControlCenter.Contracts.V1;
using AiControlCenter.Gateway.Hubs;
using MassTransit;
using Microsoft.AspNetCore.SignalR;

namespace AiControlCenter.Gateway.Messaging;

public sealed class RunStatusChangedConsumer(IHubContext<SystemHub> hub) : IConsumer<RunStatusChangedV1>
{
    public Task Consume(ConsumeContext<RunStatusChangedV1> context)
    {
        var message = context.Message;
        return hub.Clients.Groups(
                RunHubGroups.ForUser(message.OwnerUserId),
                RunHubGroups.Admins)
            .SendAsync("RunStatusChanged", message, context.CancellationToken);
    }
}
