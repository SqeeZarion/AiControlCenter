using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiControlCenter.ControlPlane.Api;
using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using AiControlCenter.ControlPlane.Infrastructure.Persistence;
using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ProductionAgentCatalogClient = AiControlCenter.Orchestrator.Api.Grpc.AgentCatalogClient;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class ControlPlaneAgentTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("control_plane_agent_tests")
        .WithUsername("postgres")
        .WithPassword("test-superuser-password")
        .Build();
    private WebApplicationFactory<ControlPlaneApiMarker>? factory;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        factory = new WebApplicationFactory<ControlPlaneApiMarker>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:ControlPlaneDatabase", postgres.GetConnectionString())
            .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath));
    }

    public async Task DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [Fact]
    public async Task MigrationSupportsLatestZeroLatestAndCreatesAgentConstraints()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync("0");
        Assert.Null(await ScalarAsync<string?>("SELECT to_regclass('control_plane.agent_definitions')::text"));
        await db.Database.MigrateAsync();
        Assert.Equal("control_plane.agent_definitions", await ScalarAsync<string?>("SELECT to_regclass('control_plane.agent_definitions')::text"));
        var constraints = await QueryStringsAsync("SELECT conname FROM pg_constraint WHERE conrelid = 'control_plane.agent_definitions'::regclass");
        Assert.Contains("ck_agent_definitions_execution_type", constraints);
        Assert.Contains("fk_agent_definitions_directions_direction_id", constraints);
        Assert.Contains("ux_agent_definitions_code", await QueryStringsAsync("SELECT indexname FROM pg_indexes WHERE schemaname='control_plane' AND tablename='agent_definitions'"));
    }

    [Fact]
    public async Task AuthorizationCrudArchiveRestoreAndStaleVersionAreEnforced()
    {
        await ClearAsync();
        var direction = await CreateDirectionAsync(CreateClient("Admin"), "agents-direction");
        using var anonymous = factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/agents")).StatusCode);
        using var user = CreateClient("User");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/v1/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/v1/agents", ValidAgent(direction.Id, "user-agent"))).StatusCode);
        using var developer = CreateClient("Developer");
        var createdResponse = await developer.PostAsJsonAsync("/v1/agents", ValidAgent(direction.Id, "test-agent"));
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<AgentDefinitionDto>(WebJson))!;
        Assert.Equal("test-agent", created.Code);
        Assert.Equal(HttpStatusCode.Conflict, (await developer.PostAsJsonAsync("/v1/agents", ValidAgent(direction.Id, "test-agent"))).StatusCode);
        using var status = await developer.PatchAsJsonAsync($"/v1/agents/{created.Id}/status", new { status = "Inactive", version = created.Version });
        status.EnsureSuccessStatusCode();
        var inactive = (await status.Content.ReadFromJsonAsync<AgentDefinitionDto>(WebJson))!;
        Assert.Equal(HttpStatusCode.Conflict, (await developer.PatchAsJsonAsync($"/v1/agents/{created.Id}/status", new { status = "Active", version = created.Version })).StatusCode);
        using var archive = await developer.PostAsJsonAsync($"/v1/agents/{created.Id}/archive", new { version = inactive.Version });
        archive.EnsureSuccessStatusCode();
        var archived = (await archive.Content.ReadFromJsonAsync<AgentDefinitionDto>(WebJson))!;
        Assert.True(archived.IsArchived);
        using var restore = await developer.PostAsJsonAsync($"/v1/agents/{created.Id}/restore", new { version = archived.Version });
        restore.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MissingRequiredFieldsAndPasswordChangeAreRejectedBeforeMutation()
    {
        await ClearAsync();
        var direction = await CreateDirectionAsync(CreateClient("Admin"), "validation-direction");
        using var developer = CreateClient("Developer");
        using var missing = await developer.PostAsJsonAsync("/v1/agents", new { directionId = direction.Id, name = "Missing code", status = "Active", executionType = "Test" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var pending = CreateClient("Developer", passwordChangeRequired: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await pending.PostAsJsonAsync("/v1/agents", ValidAgent(direction.Id, "blocked-agent"))).StatusCode);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM control_plane.agent_definitions"));
    }

    [Theory]
    [InlineData("unique-code", "unique-code-agent")]
    [InlineData("searchable description", "description-agent")]
    [InlineData("Agent display name", "name-agent")]
    public async Task SearchMatchesCodeDescriptionAndName(string search, string expectedCode)
    {
        await ClearAsync();
        using var admin = CreateClient("Admin");
        var direction = await CreateDirectionAsync(admin, "search-direction");
        await CreateAgentAsync(admin, direction.Id, "unique-code-agent", "Unrelated name", "Other description");
        await CreateAgentAsync(admin, direction.Id, "description-agent", "Another name", "Searchable description");
        await CreateAgentAsync(admin, direction.Id, "name-agent", "Agent display name", "No match here");

        var page = await admin.GetFromJsonAsync<AgentDefinitionListDto>(
            $"/v1/agents?search={Uri.EscapeDataString(search)}",
            WebJson);

        Assert.NotNull(page);
        Assert.Equal(expectedCode, Assert.Single(page.Items).Code);
    }

    [Fact]
    public async Task RunnableGrpcSnapshotRequiresBothAgentAndDirectionToBeAvailable()
    {
        await ClearAsync();
        using var admin = CreateClient("Admin");
        var direction = await CreateDirectionAsync(admin, "grpc-direction");
        var agent = await CreateAgentAsync(admin, direction.Id, "grpc-agent");
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = factory!.Server.CreateHandler() });
        var grpc = new AgentCatalog.AgentCatalogClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {TestJwtTokenFactory.Issue(role: "User")}" } };
        var reply = await grpc.GetRunnableAgentAsync(new GetRunnableAgentRequest { AgentId = agent.Id.ToString() }, headers);
        Assert.Equal(agent.Id.ToString(), reply.AgentId);

        using var deactivateAgent = await admin.PatchAsJsonAsync(
            $"/v1/agents/{agent.Id}/status", new { status = "Inactive", version = agent.Version });
        deactivateAgent.EnsureSuccessStatusCode();
        var inactiveAgent = (await deactivateAgent.Content.ReadFromJsonAsync<AgentDefinitionDto>(WebJson))!;
        var inactive = await Assert.ThrowsAsync<RpcException>(() => grpc.GetRunnableAgentAsync(
            new GetRunnableAgentRequest { AgentId = agent.Id.ToString() }, headers).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, inactive.StatusCode);
        using var reactivateAgent = await admin.PatchAsJsonAsync(
            $"/v1/agents/{agent.Id}/status", new { status = "Active", version = inactiveAgent.Version });
        reactivateAgent.EnsureSuccessStatusCode();
        var activeAgent = (await reactivateAgent.Content.ReadFromJsonAsync<AgentDefinitionDto>(WebJson))!;
        using var archiveAgent = await admin.PostAsJsonAsync(
            $"/v1/agents/{agent.Id}/archive", new { version = activeAgent.Version });
        archiveAgent.EnsureSuccessStatusCode();
        var archived = await Assert.ThrowsAsync<RpcException>(() => grpc.GetRunnableAgentAsync(
            new GetRunnableAgentRequest { AgentId = agent.Id.ToString() }, headers).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, archived.StatusCode);
        var archivedAgent = (await archiveAgent.Content.ReadFromJsonAsync<AgentDefinitionDto>(WebJson))!;
        using var restoreAgent = await admin.PostAsJsonAsync(
            $"/v1/agents/{agent.Id}/restore", new { version = archivedAgent.Version });
        restoreAgent.EnsureSuccessStatusCode();

        using var deactivate = await admin.PatchAsJsonAsync($"/v1/directions/{direction.Id}/status", new { status = "Inactive", version = direction.Version });
        deactivate.EnsureSuccessStatusCode();
        var exception = await Assert.ThrowsAsync<RpcException>(() => grpc.GetRunnableAgentAsync(new GetRunnableAgentRequest { AgentId = agent.Id.ToString() }, headers).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);

        var missing = await Assert.ThrowsAsync<RpcException>(() => grpc.GetRunnableAgentAsync(
            new GetRunnableAgentRequest { AgentId = Guid.NewGuid().ToString() }, headers).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, missing.StatusCode);

        var productionClient = new ProductionAgentCatalogClient(grpc, TimeProvider.System);
        await Assert.ThrowsAsync<AiControlCenter.Orchestrator.Application.AgentRunAgentNotFoundException>(() =>
            productionClient.GetRunnableAgentAsync(
                Guid.NewGuid(), headers.GetValue("Authorization")!, CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseRejectsUnsupportedExecutionAndDirectionDelete()
    {
        await ClearAsync();
        using var admin = CreateClient("Admin");
        var direction = await CreateDirectionAsync(admin, "constraint-direction");
        _ = await CreateAgentAsync(admin, direction.Id, "constraint-agent");
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var invalid = new NpgsqlCommand("UPDATE control_plane.agent_definitions SET execution_type='Shell'", connection);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync())).SqlState);
        await using var delete = new NpgsqlCommand("DELETE FROM control_plane.directions WHERE id=@id", connection);
        delete.Parameters.AddWithValue("id", direction.Id);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
    }

    private HttpClient CreateClient(string role, bool passwordChangeRequired = false)
    {
        var client = factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwtTokenFactory.Issue(role: role, passwordChangeRequired: passwordChangeRequired));
        return client;
    }

    private static object ValidAgent(Guid directionId, string code) => new { directionId, name = "Test Agent", code, description = "Deterministic", status = "Active", executionType = "Test" };
    private static async Task<AgentDefinitionDto> CreateAgentAsync(
        HttpClient client,
        Guid directionId,
        string code,
        string name = "Test Agent",
        string description = "Deterministic")
    {
        using var response = await client.PostAsJsonAsync(
            "/v1/agents",
            new
            {
                directionId,
                name,
                code,
                description,
                status = "Active",
                executionType = "Test",
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentDefinitionDto>(WebJson))!;
    }
    private static async Task<DirectionDto> CreateDirectionAsync(HttpClient client, string code)
    {
        using var response = await client.PostAsJsonAsync("/v1/directions", new { name = "Agent Direction", code, status = "Active", sortOrder = 1 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
    }

    private ControlPlaneDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(postgres.GetConnectionString(), npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "control_plane")).Options);
    private async Task ClearAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE control_plane.agent_definitions, control_plane.directions", connection); await command.ExecuteNonQueryAsync();
    }
    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
    private async Task<string[]> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>(); while (await reader.ReadAsync()) result.Add(reader.GetString(0)); return result.ToArray();
    }
}
