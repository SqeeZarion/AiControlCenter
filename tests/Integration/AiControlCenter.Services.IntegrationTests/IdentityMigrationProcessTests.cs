using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class IdentityMigrationProcessTests : IAsyncLifetime
{
    private const string DatabasePassword = "identity-migrator-process-test-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("identity_migrator_process_tests")
        .WithUsername("postgres")
        .WithPassword(DatabasePassword)
        .Build();

    public Task InitializeAsync() => postgres.StartAsync();

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task RealExecutableMigratesWithoutRuntimeSecretsAndNormalStartupStillFailsFast()
    {
        var migration = await RunIdentityAsync(migrate: true);

        Assert.Equal(0, migration.ExitCode);
        Assert.DoesNotContain(DatabasePassword, migration.Output, StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        Assert.True(await ScalarAsync<long>(
            connection,
            "SELECT count(*) FROM identity.\"__EFMigrationsHistory\"") > 0);
        Assert.Equal("identity.users", await ScalarAsync<string?>(
            connection,
            "SELECT to_regclass('identity.users')::text"));
        Assert.Equal(0, await ScalarAsync<long>(connection, "SELECT count(*) FROM identity.users"));
        Assert.Equal(3, await ScalarAsync<long>(connection, "SELECT count(*) FROM identity.roles"));
        Assert.Equal(0, await ScalarAsync<long>(connection, "SELECT count(*) FROM identity.user_roles"));

        var normalStartup = await RunIdentityAsync(migrate: false);

        Assert.NotEqual(0, normalStartup.ExitCode);
        Assert.Contains(nameof(Microsoft.Extensions.Options.OptionsValidationException), normalStartup.Output);
        Assert.DoesNotContain(DatabasePassword, normalStartup.Output, StringComparison.Ordinal);
    }

    private async Task<ProcessResult> RunIdentityAsync(bool migrate)
    {
        var executable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var assembly = Path.Combine(AppContext.BaseDirectory, "AiControlCenter.Identity.Api.dll");
        Assert.True(File.Exists(assembly), $"Identity executable was not found at '{assembly}'.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assembly);
        if (migrate)
        {
            startInfo.ArgumentList.Add("--migrate");
        }

        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ConnectionStrings__IdentityDatabase"] = postgres.GetConnectionString();
        startInfo.Environment["Authentication__PublicKeyPath"] = string.Empty;
        startInfo.Environment["JwtSigning__PrivateKeyPath"] = string.Empty;
        startInfo.Environment["DataProtection__KeysPath"] = string.Empty;
        startInfo.Environment["IdentityBootstrap__Email"] = string.Empty;
        startInfo.Environment["IdentityBootstrap__TemporaryPassword"] = string.Empty;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Identity process could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Identity {(migrate ? "migration" : "normal startup")} process did not exit within 60 seconds.");
        }

        return new ProcessResult(process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
