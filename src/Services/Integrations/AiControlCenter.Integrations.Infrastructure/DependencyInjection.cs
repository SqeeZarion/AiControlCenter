using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Infrastructure.Persistence;
using AiControlCenter.Integrations.Infrastructure.Providers;
using AiControlCenter.Integrations.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("IntegrationsDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:IntegrationsDatabase is required.");

        services.AddDbContext<IntegrationsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "integrations")));

        var keysPath = configuration["DataProtection:KeysPath"]
            ?? throw new InvalidOperationException("DataProtection:KeysPath is required.");
        services.AddDataProtection()
            .SetApplicationName("AiControlCenter.Integrations")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IProviderConnectionRepository, ProviderConnectionRepository>();
        services.AddScoped<IProviderOAuthStateRepository, ProviderOAuthStateRepository>();
        services.AddScoped<IProviderExecutionReceiptRepository, ProviderExecutionReceiptRepository>();
        services.AddScoped<IProviderBudgetManager, ProviderBudgetManager>();
        services.AddScoped<IIntegrationsUnitOfWork>(provider => provider.GetRequiredService<IntegrationsDbContext>());
        services.AddScoped<IProviderCredentialProtector, ProviderCredentialProtector>();
        services.AddScoped<ProviderConnectionService>();
        services.AddOptions<ProviderConnectionFeatureOptions>()
            .Bind(configuration.GetSection(ProviderConnectionFeatureOptions.SectionName));
        services.AddScoped<ProviderConnectionFeatureAccess>();
        services.AddSingleton<IValidateOptions<GitHubOAuthOptions>, GitHubOAuthOptionsValidator>();
        services.AddOptions<GitHubOAuthOptions>()
            .Bind(configuration.GetSection(GitHubOAuthOptions.SectionName))
            .ValidateOnStart();
        services.AddHttpClient(nameof(GitHubOAuthService));
        services.AddScoped<GitHubOAuthService>();
        services.AddSingleton<IValidateOptions<LocalCopilotCliOptions>, LocalCopilotCliOptionsValidator>();
        services.AddOptions<LocalCopilotCliOptions>()
            .Bind(configuration.GetSection(LocalCopilotCliOptions.SectionName))
            .ValidateOnStart();
        services.AddScoped<LocalCopilotCliAccess>();
        services.AddHttpClient<LocalCopilotReconnectClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<LocalCopilotCliOptions>>().Value;
            client.BaseAddress = new Uri(options.ManagementEndpoint);
            client.Timeout = TimeSpan.FromSeconds(Math.Min(options.RequestTimeoutSeconds, 30));
        });
        services.AddScoped<ILocalCopilotReconnectStateClient>(provider =>
            provider.GetRequiredService<LocalCopilotReconnectClient>());
        services.AddScoped<ILocalCopilotConnectionCompletion, LocalCopilotConnectionCompletion>();
        services.AddScoped<LocalCopilotReconnectWorkflow>();
        services.AddSingleton<IToolExecutor, SafeToolExecutor>();
        services.AddScoped<ICopilotRuntime, OfficialCopilotRuntime>();
        services.AddScoped<ILocalCopilotRuntime, OfficialLocalCopilotRuntime>();
        services.AddScoped<DeterministicTestProvider>();
        services.AddHttpClient<GitHubCopilotProvider>();
        services.AddHttpClient<OpenAiProvider>(client =>
            client.BaseAddress = new Uri(configuration["Providers:OpenAi:BaseUrl"] ?? "https://api.openai.com/"));
        services.AddHttpClient<XAiProvider>(client =>
            client.BaseAddress = new Uri(configuration["Providers:XAi:BaseUrl"] ?? "https://api.x.ai/"));
        services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<DeterministicTestProvider>());
        services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<GitHubCopilotProvider>());
        services.AddScoped<IAiProvider, GitHubCopilotLocalCliProvider>();
        services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<OpenAiProvider>());
        services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<XAiProvider>());

        return services;
    }
}
