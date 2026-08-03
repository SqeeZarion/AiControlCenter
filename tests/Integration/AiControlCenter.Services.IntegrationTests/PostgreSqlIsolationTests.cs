using AiControlCenter.ControlPlane.Infrastructure.Persistence;
using AiControlCenter.Identity.Infrastructure.Persistence;
using AiControlCenter.Integrations.Infrastructure.Persistence;
using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class PostgreSqlIsolationTests
{
    [Fact]
    public async Task DataOwnerRolesConnectAndCannotAccessAnotherSchema()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
            .WithDatabase("aicontrolcenter_tests")
            .WithUsername("postgres")
            .WithPassword("test-superuser-password")
            .Build();

        await postgres.StartAsync();
        await InitializeOwnersAsync(postgres.GetConnectionString());

        var identity = BuildConnectionString(postgres.GetConnectionString(), "identity_user", "identity-password", "identity");
        var controlPlane = BuildConnectionString(postgres.GetConnectionString(), "controlplane_user", "controlplane-password", "control_plane");
        var orchestrator = BuildConnectionString(postgres.GetConnectionString(), "orchestrator_user", "orchestrator-password", "orchestrator");
        var integrations = BuildConnectionString(postgres.GetConnectionString(), "integrations_user", "integrations-password", "integrations");

        await AssertDbContextsConnectAsync(identity, controlPlane, orchestrator, integrations);
        await CreateProbeAsync(identity);
        await CreateProbeAsync(controlPlane);

        await using var identityConnection = new NpgsqlConnection(identity);
        await identityConnection.OpenAsync();
        await using var forbidden = new NpgsqlCommand("SELECT id FROM control_plane.isolation_probe", identityConnection);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteScalarAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static async Task InitializeOwnersAsync(string administratorConnectionString)
    {
        const string sql = """
            CREATE ROLE identity_user LOGIN PASSWORD 'identity-password';
            CREATE ROLE controlplane_user LOGIN PASSWORD 'controlplane-password';
            CREATE ROLE orchestrator_user LOGIN PASSWORD 'orchestrator-password';
            CREATE ROLE integrations_user LOGIN PASSWORD 'integrations-password';
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            CREATE SCHEMA identity AUTHORIZATION identity_user;
            CREATE SCHEMA control_plane AUTHORIZATION controlplane_user;
            CREATE SCHEMA orchestrator AUTHORIZATION orchestrator_user;
            CREATE SCHEMA integrations AUTHORIZATION integrations_user;
            ALTER ROLE identity_user SET search_path TO identity;
            ALTER ROLE controlplane_user SET search_path TO control_plane;
            ALTER ROLE orchestrator_user SET search_path TO orchestrator;
            ALTER ROLE integrations_user SET search_path TO integrations;
            """;

        await using var connection = new NpgsqlConnection(administratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertDbContextsConnectAsync(
        string identity,
        string controlPlane,
        string orchestrator,
        string integrations)
    {
        await using var identityContext = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(identity).Options);
        await using var controlPlaneContext = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(controlPlane).Options);
        await using var orchestratorContext = new OrchestratorDbContext(
            new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(orchestrator).Options);
        await using var integrationsContext = new IntegrationsDbContext(
            new DbContextOptionsBuilder<IntegrationsDbContext>().UseNpgsql(integrations).Options);

        Assert.True(await identityContext.Database.CanConnectAsync());
        Assert.True(await controlPlaneContext.Database.CanConnectAsync());
        Assert.True(await orchestratorContext.Database.CanConnectAsync());
        Assert.True(await integrationsContext.Database.CanConnectAsync());
    }

    private static async Task CreateProbeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "CREATE TABLE isolation_probe (id integer NOT NULL); INSERT INTO isolation_probe VALUES (1);",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildConnectionString(
        string administratorConnectionString,
        string username,
        string password,
        string searchPath)
    {
        var builder = new NpgsqlConnectionStringBuilder(administratorConnectionString)
        {
            Username = username,
            Password = password,
            SearchPath = searchPath,
        };
        return builder.ConnectionString;
    }
}
