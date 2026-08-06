using AiControlCenter.Identity.Application;

namespace AiControlCenter.Identity.Api;

// Це фоновий сервіс, який запускається один раз під час старту Identity API.
// Його завдання — гарантувати, що в системі існує хоча б один активний Admin.
public sealed partial class BootstrapAdminHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<BootstrapAdminHostedService> logger) : IHostedService
{
    //Bootstrap створює першого адміністратора з конфігурації.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Перший запуск  → Admin створюється
        // Другий запуск  → Admin уже є, нічого не змінюється
        // Перезапуск     → дублікат не створюється
        if (configuration.GetValue<bool>("IdentityBootstrap:Disabled"))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        if (await users.CountActiveAdminsAsync(cancellationToken) > 0)
        {
            return;
        }

        var email = ReadSecret("IdentityBootstrap:Email", "IdentityBootstrap:EmailFile");
        var password = ReadSecret("IdentityBootstrap:TemporaryPassword", "IdentityBootstrap:TemporaryPasswordFile");
        var displayName = configuration["IdentityBootstrap:DisplayName"] ?? "Platform Administrator";

        //Різниця Development і Production
        // Це fail-fast захист. Production не повинен випадково запуститися без адміністратора.

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            if (environment.IsDevelopment())
            {
                LogBootstrapSkipped(logger);
                return;
            }

            throw new InvalidOperationException(
                "An active Admin or IdentityBootstrap secret-file configuration is required.");
        }

        //Тимчасовий пароль повинен містити від 12 до 128 символів.
        if (password.Length is < 12 or > 128)
        {
            throw new InvalidOperationException("The bootstrap temporary password must contain 12 to 128 characters.");
        }

        // Створення адміністратора
        // API не створює сутність і не хешує пароль самостійно. Він передає дію в Application:
        var service = scope.ServiceProvider.GetRequiredService<IdentityApplicationService>();
        if (await service.BootstrapAdminAsync(email, displayName, password, cancellationToken))
        {
            LogAdminCreated(logger);
        }
    }

    //bootstrap не тримає постійних ресурсів або циклів.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string? ReadSecret(string valueKey, string fileKey)
    {
        var file = configuration[fileKey];
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"Configured secret file for {valueKey} was not found.", file);
            }

            return File.ReadAllText(file).Trim();
        }

        return configuration[valueKey]?.Trim();
    }

    //.NET генерує реалізацію логування під час компіляції.
    [LoggerMessage(EventId = 2200, Level = LogLevel.Warning, Message =
        "No active Admin exists and Development bootstrap secrets are not configured")]
    private static partial void LogBootstrapSkipped(ILogger logger);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message =
        "The initial Admin account was bootstrapped and requires a password change")]
    private static partial void LogAdminCreated(ILogger logger);
}
