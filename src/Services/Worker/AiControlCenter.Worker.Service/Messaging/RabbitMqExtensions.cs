using MassTransit;

namespace AiControlCenter.Worker.Service.Messaging;

public static class RabbitMqExtensions
{
    public static IServiceCollection AddRabbitMqTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMassTransit(configurator =>
        {
            configurator.SetKebabCaseEndpointNameFormatter();
            configurator.UsingRabbitMq((context, rabbit) =>
            {
                var host = configuration["RabbitMq:Host"] ?? "rabbitmq";
                var port = configuration.GetValue<ushort?>("RabbitMq:Port") ?? 5672;
                var user = configuration["RabbitMq:Username"] ?? "guest";
                var password = configuration["RabbitMq:Password"] ?? "guest";

                rabbit.Host(host, port, "/", credentials =>
                {
                    credentials.Username(user);
                    credentials.Password(password);
                });
                rabbit.UseMessageRetry(retry => retry.Intervals(
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5)));
                rabbit.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
