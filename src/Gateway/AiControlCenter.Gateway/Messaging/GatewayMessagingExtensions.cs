using MassTransit;

namespace AiControlCenter.Gateway.Messaging;

public static class GatewayMessagingExtensions
{
    public static IServiceCollection AddGatewayMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!configuration.GetValue("RabbitMq:Enabled", false)) return services;
        var host = configuration["RabbitMq:Host"]
            ?? throw new InvalidOperationException("RabbitMq:Host is required when messaging is enabled.");
        var user = configuration["RabbitMq:Username"]
            ?? throw new InvalidOperationException("RabbitMq:Username is required when messaging is enabled.");
        var password = configuration["RabbitMq:Password"]
            ?? throw new InvalidOperationException("RabbitMq:Password is required when messaging is enabled.");
        var port = configuration.GetValue<ushort?>("RabbitMq:Port") ?? 5672;

        services.AddMassTransit(configurator =>
        {
            configurator.AddConsumer<RunStatusChangedConsumer>();
            configurator.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(host, port, "/", credentials =>
                {
                    credentials.Username(user);
                    credentials.Password(password);
                });
                rabbit.ReceiveEndpoint("aicontrolcenter-gateway-run-status-v1", endpoint =>
                {
                    endpoint.UseMessageRetry(retry => retry.Intervals(
                        TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
                    endpoint.ConfigureConsumer<RunStatusChangedConsumer>(context);
                });
            });
        });
        return services;
    }
}
