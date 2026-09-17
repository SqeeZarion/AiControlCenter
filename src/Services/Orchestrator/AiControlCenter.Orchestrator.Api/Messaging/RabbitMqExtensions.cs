using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using MassTransit;

namespace AiControlCenter.Orchestrator.Api.Messaging;

// Цей метод:
//
// реєструє MassTransit у Dependency Injection;
// створює шину повідомлень;
// керує запуском і зупинкою підключення до RabbitMQ разом із застосунком;
// дозволяє використовувати IPublishEndpoint, ISendEndpointProvider та consumers.

public static class RabbitMqExtensions
{
    public static IServiceCollection AddRabbitMqTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMassTransit(configurator =>
        {
            configurator.SetKebabCaseEndpointNameFormatter();
            configurator.AddEntityFrameworkOutbox<OrchestratorDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromMilliseconds(250);
            });
            configurator.AddConsumer<AgentRunExecutionFaultConsumer>();
            configurator.UsingRabbitMq((context, rabbit) =>
            {
                //DNS-ім’я контейнера RabbitMQ;
                var host = configuration["RabbitMq:Host"] ?? "rabbitmq";
                var port = configuration.GetValue<ushort?>("RabbitMq:Port") ?? 5672;
                //логін і пароль;
                var user = configuration["RabbitMq:Username"] ?? "guest";
                var password = configuration["RabbitMq:Password"] ?? "guest";

                //налаштовується з’єднання
                rabbit.Host(host, port, "/", credentials =>
                {
                    credentials.Username(user);
                    credentials.Password(password);
                });
                rabbit.UseMessageRetry(retry => retry.Intervals(
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5)));
                rabbit.ReceiveEndpoint("aicontrolcenter-orchestrator-run-faults-v1", endpoint =>
                {
                    endpoint.UseEntityFrameworkOutbox<OrchestratorDbContext>(context);
                    endpoint.ConfigureConsumer<AgentRunExecutionFaultConsumer>(context);
                });
            });
        });

        return services;
    }
}
