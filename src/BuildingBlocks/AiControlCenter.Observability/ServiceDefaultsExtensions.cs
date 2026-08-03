using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AiControlCenter.Observability;

// Додає стандартні технічні налаштування сервісам
//Файл налаштовує:
// логування;
// OpenTelemetry;
// метрики;
// tracing;
// health checks;
// обробку помилок;
// Correlation ID.

public static class ServiceDefaultsExtensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();

        //Ідентифікація сервісу
        var resource = ResourceBuilder.CreateDefault()
            .AddService(builder.Environment.ApplicationName);

        // Збирає:
        // інформацію про вхідні HTTP-запити;
        // інформацію про вихідні HTTP-запити;
        // використання пам’яті;
        // роботу Garbage Collector;
        // стан .NET Runtime.
        //Записує шлях запиту між сервісами. Tracing
        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder.AddService(builder.Environment.ApplicationName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            openTelemetry.WithMetrics(metrics => metrics.AddOtlpExporter());
            openTelemetry.WithTracing(tracing => tracing.AddOtlpExporter());
            builder.Logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resource);
                options.AddOtlpExporter();
            });
        }

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return builder;
    }

    public static IServiceCollection AddApiFoundation(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }

    //Додає зареєстровані компоненти в HTTP pipeline.

    public static WebApplication UseApiFoundation(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        return app;
    }

    //Створює два HTTP-маршрути.
    //Перевіряє, чи живий процес.
    //Перевіряє, чи сервіс готовий працювати із залежностями.

    public static IEndpointRouteBuilder MapDefaultHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
            ResponseWriter = WriteHealthResponseAsync,
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => !registration.Tags.Contains("live"),
            ResponseWriter = WriteHealthResponseAsync,
        });

        return endpoints;
    }

    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                duration = entry.Value.Duration.TotalMilliseconds,
            }),
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, payload);
    }
}
