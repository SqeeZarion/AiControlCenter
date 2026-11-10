using AiControlCenter.ControlPlane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class ControlPlaneMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("control_plane_migration_tests")
        .WithUsername("postgres")
        .WithPassword("test-superuser-password")
        .Build();

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task LatestToZeroToLatestPreservesAiAgentSchema()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        await AssertLatestSchemaAsync();
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("0");
        Assert.Null(await ScalarAsync<string?>("SELECT to_regclass('control_plane.agent_definitions')::text"));
        await db.Database.MigrateAsync();
        await AssertLatestSchemaAsync();
    }

    [Fact]
    public void CurrentModelHasNoPendingChanges()
    {
        using var db = CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private ControlPlaneDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), options =>
                options.MigrationsHistoryTable("__EFMigrationsHistory", "control_plane"))
            .Options);

    private async Task AssertLatestSchemaAsync()
    {
        Assert.Equal("control_plane.agent_definitions",
            await ScalarAsync<string?>("SELECT to_regclass('control_plane.agent_definitions')::text"));
        Assert.Equal("control_plane.directions",
            await ScalarAsync<string?>("SELECT to_regclass('control_plane.directions')::text"));
        Assert.Equal("jsonb", await ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_schema='control_plane' "
            + "AND table_name='agent_definitions' AND column_name='model_routes_json'"));
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
