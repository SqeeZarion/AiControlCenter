using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.RabbitMq;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class RabbitMqConnectivityTests
{
    [Fact]
    public async Task MassTransitCanPublishAndConsumeATestOnlyContract()
    {
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4.1.4-management-alpine")
            .WithUsername("foundation_test")
            .WithPassword("foundation-test-password")
            .Build();

        await rabbitMq.StartAsync();

        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(configurator =>
            {
                configurator.AddConsumer<InfrastructureProbeConsumer>();
                configurator.UsingRabbitMq((context, rabbit) =>
                {
                    rabbit.Host(new Uri(rabbitMq.GetConnectionString()));
                    rabbit.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var message = new InfrastructureProbe(Guid.NewGuid());
            await harness.Bus.Publish(message);

            Assert.True(await harness.Consumed.Any<InfrastructureProbe>());
            var consumer = harness.GetConsumerHarness<InfrastructureProbeConsumer>();
            Assert.True(await consumer.Consumed.Any<InfrastructureProbe>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    private sealed record InfrastructureProbe(Guid CorrelationId);

    private sealed class InfrastructureProbeConsumer : IConsumer<InfrastructureProbe>
    {
        public Task Consume(ConsumeContext<InfrastructureProbe> context) => Task.CompletedTask;
    }
}
