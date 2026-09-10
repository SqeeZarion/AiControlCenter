using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiControlCenter.ControlPlane.Api;
using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;
using AiControlCenter.ControlPlane.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class ControlPlaneDirectionTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("control_plane_direction_tests")
        .WithUsername("postgres")
        .WithPassword("test-superuser-password")
        .Build();
    private WebApplicationFactory<ControlPlaneApiMarker>? factory;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
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
    public async Task MigrationSupportsLatestZeroLatestAndCreatesExpectedDatabaseObjects()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync("0");
        Assert.Null(await ScalarAsync<string?>("SELECT to_regclass('control_plane.directions')::text"));
        await dbContext.Database.MigrateAsync();
        Assert.Equal(
            "control_plane.directions",
            await ScalarAsync<string?>("SELECT to_regclass('control_plane.directions')::text"));
        var constraints = await QueryStringsAsync("SELECT conname FROM pg_constraint WHERE conrelid = 'control_plane.directions'::regclass");
        Assert.Contains("ck_directions_code_format", constraints);
        Assert.Contains("ck_directions_name_length", constraints);
        Assert.Contains("ck_directions_sort_order", constraints);
        Assert.Contains("ck_directions_status", constraints);
        var indexes = await QueryStringsAsync("SELECT indexname FROM pg_indexes WHERE schemaname = 'control_plane' AND tablename = 'directions'");
        Assert.Contains("ux_directions_code", indexes);
        Assert.Contains("ix_directions_list", indexes);
    }

    [Fact]
    public async Task AdminCanCompleteCrudArchiveFilterAndRestoreFlow()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        var created = await CreateAsync(client, "  AI Notes  ", "AI_Notes", 20);
        Assert.Equal("AI Notes", created.Name);
        Assert.Equal("ai-notes", created.Code);

        var list = await client.GetFromJsonAsync<DirectionListDto>("/v1/directions", WebJson);
        Assert.Single(list!.Items);
        using var update = await client.PutAsJsonAsync($"/v1/directions/{created.Id}", new
        {
            name = "Ideas Lab",
            code = "ideas-lab",
            description = "Structured analysis",
            icon = "spark",
            status = "Active",
            sortOrder = 20,
            version = created.Version,
        });
        update.EnsureSuccessStatusCode();
        var updated = (await update.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        using var status = await client.PatchAsJsonAsync($"/v1/directions/{created.Id}/status", new { status = "Inactive", version = updated.Version });
        status.EnsureSuccessStatusCode();
        var inactive = (await status.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        using var order = await client.PatchAsJsonAsync($"/v1/directions/{created.Id}/sort-order", new { sortOrder = 3, version = inactive.Version });
        order.EnsureSuccessStatusCode();
        var reordered = (await order.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        using var archive = await client.PostAsJsonAsync($"/v1/directions/{created.Id}/archive", new { version = reordered.Version });
        archive.EnsureSuccessStatusCode();
        var archived = (await archive.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        Assert.True(archived.IsArchived);
        Assert.Empty((await client.GetFromJsonAsync<DirectionListDto>("/v1/directions", WebJson))!.Items);
        Assert.Single((await client.GetFromJsonAsync<DirectionListDto>(
            "/v1/directions?includeArchived=true&status=Inactive&search=ideas",
            WebJson))!.Items);
        using var restore = await client.PostAsJsonAsync($"/v1/directions/{created.Id}/restore", new { version = archived.Version });
        restore.EnsureSuccessStatusCode();
        var persisted = await client.GetFromJsonAsync<DirectionDto>($"/v1/directions/{created.Id}", WebJson);
        Assert.Equal("Ideas Lab", persisted!.Name);
        Assert.Equal(3, persisted.SortOrder);
        Assert.False(persisted.IsArchived);
    }

    [Fact]
    public async Task AuthorizationMatrixIsFailClosed()
    {
        await ClearDirectionsAsync();
        using var anonymous = factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/directions")).StatusCode);
        using var user = CreateClient("User");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/v1/directions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/v1/directions", ValidCreate("user-direction"))).StatusCode);
        using var developer = CreateClient("Developer");
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.PostAsJsonAsync("/v1/directions", ValidCreate("developer-direction"))).StatusCode);
        using var passwordPending = CreateClient("Admin", passwordChangeRequired: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await passwordPending.GetAsync("/v1/directions")).StatusCode);
        using var admin = CreateClient("Admin");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/v1/directions", ValidCreate("admin-direction"))).StatusCode);
    }

    [Fact]
    public async Task ValidationDuplicateNotFoundArchivedAndConcurrencyConflictsUseExpectedStatuses()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        var created = await CreateAsync(client, "Direction", "unique-code", 1);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/v1/directions", ValidCreate("unique-code"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/v1/directions", new { name = "x", code = "bad/code", status = "Active", sortOrder = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/directions/{Guid.NewGuid()}")).StatusCode);

        using var firstUpdate = await client.PutAsJsonAsync($"/v1/directions/{created.Id}", new { name = "First", code = "first", description = (string?)null, icon = (string?)null, status = "Active", sortOrder = 1, version = created.Version });
        firstUpdate.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/v1/directions/{created.Id}", new { name = "Stale", code = "stale", description = (string?)null, icon = (string?)null, status = "Active", sortOrder = 1, version = created.Version })).StatusCode);
        var current = (await firstUpdate.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        using var archive = await client.PostAsJsonAsync(
            $"/v1/directions/{created.Id}/archive",
            new { version = current.Version });
        archive.EnsureSuccessStatusCode();
        var archived = (await archive.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/v1/directions/{created.Id}", new { name = "Blocked", code = "blocked", description = (string?)null, icon = (string?)null, status = "Active", sortOrder = 1, version = archived.Version })).StatusCode);
    }

    [Fact]
    public async Task DatabaseConstraintsRejectInvalidRowsAndCodesRemainUniqueAcrossArchive()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        var created = await CreateAsync(client, "Kept", "kept-code", 1);
        _ = await client.PostAsJsonAsync($"/v1/directions/{created.Id}/archive", new { version = created.Version });
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/v1/directions", ValidCreate("kept-code"))).StatusCode);
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO control_plane.directions (id,name,code,status,sort_order,created_at,updated_at) VALUES (@id,'Bad','bad','Unknown',-1,now(),now())", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var shortCode = new NpgsqlCommand(
            "INSERT INTO control_plane.directions (id,name,code,status,sort_order,created_at,updated_at) "
            + "VALUES (@id,'Valid','x','Active',1,now(),now())",
            connection);
        shortCode.Parameters.AddWithValue("id", Guid.NewGuid());
        var shortCodeException = await Assert.ThrowsAsync<PostgresException>(() => shortCode.ExecuteNonQueryAsync());
        Assert.Equal("ck_directions_code_format", shortCodeException.ConstraintName);

        await using var blankName = new NpgsqlCommand(
            "INSERT INTO control_plane.directions (id,name,code,status,sort_order,created_at,updated_at) "
            + "VALUES (@id,' ','valid-name','Active',1,now(),now())",
            connection);
        blankName.Parameters.AddWithValue("id", Guid.NewGuid());
        var blankNameException = await Assert.ThrowsAsync<PostgresException>(() => blankName.ExecuteNonQueryAsync());
        Assert.Equal("ck_directions_name_length", blankNameException.ConstraintName);
    }

    [Fact]
    public async Task UnicodeNameValidationReturnsBadRequestBeforePostgreSqlMutation()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");

        using var response = await client.PostAsJsonAsync(
            "/v1/directions",
            new { name = "😀", code = "single-emoji", status = "Active", sortOrder = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM control_plane.directions"));
    }

    [Fact]
    public async Task UnicodeNameBoundariesAreConsistentThroughApiAndPostgreSql()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        var boundaryName = string.Concat(Enumerable.Repeat("😀", Direction.NameMaxLength));

        using var boundary = await client.PostAsJsonAsync(
            "/v1/directions",
            new { name = boundaryName, code = "emoji-boundary", status = "Active", sortOrder = 0 });
        Assert.Equal(HttpStatusCode.Created, boundary.StatusCode);
        Assert.Equal(boundaryName, (await boundary.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!.Name);

        using var combining = await client.PostAsJsonAsync(
            "/v1/directions",
            new { name = "e\u0301", code = "combining-name", status = "Active", sortOrder = 1 });
        Assert.Equal(HttpStatusCode.Created, combining.StatusCode);

        using var overBoundary = await client.PostAsJsonAsync(
            "/v1/directions",
            new
            {
                name = boundaryName + "😀",
                code = "emoji-over-boundary",
                status = "Active",
                sortOrder = 2,
            });
        Assert.Equal(HttpStatusCode.BadRequest, overBoundary.StatusCode);
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM control_plane.directions"));
    }

    [Fact]
    public async Task UnicodeWhiteSpaceNormalizationIsConsistentThroughApiAndPostgreSql()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");

        using var onlyWhiteSpace = await client.PostAsJsonAsync(
            "/v1/directions",
            new { name = "\u0085", code = "unicode-white-space", status = "Active", sortOrder = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, onlyWhiteSpace.StatusCode);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM control_plane.directions"));

        using var mixedWhiteSpace = await client.PostAsJsonAsync(
            "/v1/directions",
            new
            {
                name = "\u0085A\u00A0\u2007\u202FB\u0085",
                code = "normalized-white-space",
                status = "Active",
                sortOrder = 1,
            });
        Assert.Equal(HttpStatusCode.Created, mixedWhiteSpace.StatusCode);
        Assert.Equal("A B", (await mixedWhiteSpace.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!.Name);

        using var byteOrderMark = await client.PostAsJsonAsync(
            "/v1/directions",
            new { name = "\uFEFFA", code = "byte-order-mark", status = "Active", sortOrder = 2 });
        Assert.Equal(HttpStatusCode.Created, byteOrderMark.StatusCode);
        Assert.Equal("\uFEFFA", (await byteOrderMark.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!.Name);
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM control_plane.directions"));
    }

    [Fact]
    public async Task UpdateChangesAllEditableFieldsAtomicallyInOneRequest()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        var created = await CreateAsync(client, "Original", "original", 1);

        using var response = await client.PutAsJsonAsync($"/v1/directions/{created.Id}", new
        {
            name = "Updated",
            code = "updated",
            description = "New description",
            icon = "new-icon",
            status = "Inactive",
            sortOrder = 42,
            version = created.Version,
        });

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        Assert.Equal("Updated", updated.Name);
        Assert.Equal("updated", updated.Code);
        Assert.Equal("New description", updated.Description);
        Assert.Equal("new-icon", updated.Icon);
        Assert.Equal(DirectionStatus.Inactive, updated.Status);
        Assert.Equal(42, updated.SortOrder);
    }

    [Fact]
    public async Task FailedAtomicUpdatesLeaveEveryPersistedFieldUnchanged()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        var original = await CreateAsync(client, "Original", "original", 1);
        _ = await CreateAsync(client, "Reserved", "reserved", 2);

        using var duplicate = await client.PutAsJsonAsync($"/v1/directions/{original.Id}", new
        {
            name = "Should not persist",
            code = "reserved",
            description = "Should not persist",
            icon = "should-not-persist",
            status = "Inactive",
            sortOrder = 99,
            version = original.Version,
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await AssertDirectionUnchangedAsync(client, original);

        using var invalid = await client.PutAsJsonAsync($"/v1/directions/{original.Id}", new
        {
            name = "Still should not persist",
            code = "still-should-not-persist",
            description = "Still should not persist",
            icon = "still-should-not-persist",
            status = "Unknown",
            sortOrder = 100,
            version = original.Version,
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await AssertDirectionUnchangedAsync(client, original);

        using var stale = await client.PutAsJsonAsync($"/v1/directions/{original.Id}", new
        {
            name = "Stale",
            code = "stale",
            description = "Stale",
            icon = "stale",
            status = "Inactive",
            sortOrder = 42,
            version = original.Version + 1,
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertDirectionUnchangedAsync(client, original);
    }

    [Fact]
    public async Task AtomicUpdateRejectsMissingRequiredPropertiesWithoutChangingTheRow()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        var original = await CreateAsync(client, "Original", "required-fields", 17);

        var payloads = new (object Payload, string MissingField)[]
        {
            (new
            {
                code = "missing-name",
                description = "Changed",
                icon = "changed",
                status = "Inactive",
                sortOrder = 3,
                version = original.Version,
            }, "Name"),
            (new
            {
                name = "Missing code",
                description = "Changed",
                icon = "changed",
                status = "Inactive",
                sortOrder = 3,
                version = original.Version,
            }, "Code"),
            (new
            {
                name = "Missing status",
                code = "missing-status",
                description = "Changed",
                icon = "changed",
                sortOrder = 3,
                version = original.Version,
            }, "Status"),
            (new
            {
                name = "Missing sort order",
                code = "missing-sort-order",
                description = "Changed",
                icon = "changed",
                status = "Inactive",
                version = original.Version,
            }, "SortOrder"),
            (new
            {
                name = "Missing version",
                code = "missing-version",
                description = "Changed",
                icon = "changed",
                status = "Inactive",
                sortOrder = 3,
            }, "Version"),
        };

        foreach (var (payload, missingField) in payloads)
        {
            using var response = await client.PutAsJsonAsync($"/v1/directions/{original.Id}", payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(missingField, out _));
            await AssertDirectionUnchangedAsync(client, original);
        }

        using var explicitDefaults = await client.PutAsJsonAsync($"/v1/directions/{original.Id}", new
        {
            name = "Explicit defaults",
            code = "explicit-defaults",
            description = (string?)null,
            icon = (string?)null,
            status = "Inactive",
            sortOrder = 0,
            version = original.Version,
        });
        explicitDefaults.EnsureSuccessStatusCode();
        var updated = (await explicitDefaults.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
        Assert.Equal(DirectionStatus.Inactive, updated.Status);
        Assert.Equal(0, updated.SortOrder);
    }

    [Fact]
    public async Task CreateReturnsCreatedDtoWithoutAContextDependentLocation()
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");

        using var response = await client.PostAsJsonAsync("/v1/directions", ValidCreate("location"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;

        Assert.Null(response.Headers.Location);
        Assert.Equal(created.Id, (await client.GetFromJsonAsync<DirectionDto>(
            $"/v1/directions/{created.Id}",
            WebJson))!.Id);
    }

    [Fact]
    public async Task ListRejectsInvalidPaginationValues()
    {
        using var client = CreateClient("User");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/directions?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/directions?pageSize=101")).StatusCode);
    }

    [Fact]
    public async Task PaginationUsesBoundedDefaultsMetadataAndDeterministicBoundaries()
    {
        await ClearDirectionsAsync();
        await SeedDirectionsAsync(25);
        using var client = CreateClient("User");

        var defaultPage = await client.GetFromJsonAsync<DirectionListDto>("/v1/directions", WebJson);
        Assert.NotNull(defaultPage);
        Assert.Equal(1, defaultPage.Page);
        Assert.Equal(20, defaultPage.PageSize);
        Assert.Equal(25, defaultPage.TotalCount);
        Assert.Equal(2, defaultPage.TotalPages);
        Assert.Equal(20, defaultPage.Items.Count);

        var fullPage = await client.GetFromJsonAsync<DirectionListDto>(
            "/v1/directions?page=1&pageSize=100",
            WebJson);
        var secondPage = await client.GetFromJsonAsync<DirectionListDto>(
            "/v1/directions?page=2&pageSize=10",
            WebJson);
        var repeatedSecondPage = await client.GetFromJsonAsync<DirectionListDto>(
            "/v1/directions?page=2&pageSize=10",
            WebJson);
        Assert.NotNull(fullPage);
        Assert.NotNull(secondPage);
        Assert.NotNull(repeatedSecondPage);
        Assert.Equal(25, fullPage.Items.Count);
        Assert.Equal(
            fullPage.Items.Skip(10).Take(10).Select(item => item.Id),
            secondPage.Items.Select(item => item.Id));
        Assert.Equal(
            secondPage.Items.Select(item => item.Id),
            repeatedSecondPage.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData("%", "literal-percent")]
    [InlineData("_", "literal-underscore")]
    [InlineData("\\", "literal-backslash")]
    public async Task SearchTreatsLikeMetacharactersAsLiterals(string search, string expectedCode)
    {
        await ClearDirectionsAsync();
        using var client = CreateClient("Admin");
        await CreateAsync(client, "Literal %", "literal-percent", 1);
        await CreateAsync(client, "Literal _", "literal-underscore", 2);
        await CreateAsync(client, "Literal \\ slash", "literal-backslash", 3);
        await CreateAsync(client, "Literal wildcard", "literal-wildcard", 4);

        var result = await client.GetFromJsonAsync<DirectionListDto>(
            $"/v1/directions?search={Uri.EscapeDataString(search)}&pageSize=100",
            WebJson);

        Assert.NotNull(result);
        Assert.Equal(expectedCode, Assert.Single(result.Items).Code);
    }

    [Fact]
    public async Task ConcurrentDuplicateCodeRequestsAreResolvedByDatabaseUniqueness()
    {
        await ClearDirectionsAsync();
        await using var coordinatedFactory = CreateCoordinatedFactory();
        using var firstClient = CreateClient(coordinatedFactory, "Admin");
        using var secondClient = CreateClient(coordinatedFactory, "Admin");

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/v1/directions", ValidCreate("raced-code")),
            secondClient.PostAsJsonAsync("/v1/directions", ValidCreate("raced-code")))
            .WaitAsync(TimeSpan.FromSeconds(30));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM control_plane.directions WHERE code = 'raced-code'"));
    }

    [Fact]
    public async Task ConcurrentUpdateAndArchiveWithSameVersionAllowOnlyOneMutation()
    {
        await ClearDirectionsAsync();
        using var setupClient = CreateClient("Admin");
        var created = await CreateAsync(setupClient, "Concurrent", "concurrent", 1);
        await using var coordinatedFactory = CreateCoordinatedFactory();
        using var updateClient = CreateClient(coordinatedFactory, "Admin");
        using var archiveClient = CreateClient(coordinatedFactory, "Admin");

        var responses = await Task.WhenAll(
            updateClient.PutAsJsonAsync($"/v1/directions/{created.Id}", new
            {
                name = "Updated concurrently",
                code = "updated-concurrently",
                description = "Updated",
                icon = "updated",
                status = "Inactive",
                sortOrder = 9,
                version = created.Version,
            }),
            archiveClient.PostAsJsonAsync(
                $"/v1/directions/{created.Id}/archive",
                new { version = created.Version }))
            .WaitAsync(TimeSpan.FromSeconds(30));
        using var updateResponse = responses[0];
        using var archiveResponse = responses[1];

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());

        using var verificationClient = CreateClient("Admin");
        var persisted = await verificationClient.GetFromJsonAsync<DirectionDto>(
            $"/v1/directions/{created.Id}",
            WebJson);
        Assert.NotNull(persisted);
        if (updateResponse.StatusCode == HttpStatusCode.OK)
        {
            Assert.False(persisted.IsArchived);
            Assert.Equal("Updated concurrently", persisted.Name);
            Assert.Equal("updated-concurrently", persisted.Code);
            Assert.Equal("Updated", persisted.Description);
            Assert.Equal("updated", persisted.Icon);
            Assert.Equal(DirectionStatus.Inactive, persisted.Status);
            Assert.Equal(9, persisted.SortOrder);
        }
        else
        {
            Assert.True(persisted.IsArchived);
            Assert.Equal(created.Name, persisted.Name);
            Assert.Equal(created.Code, persisted.Code);
            Assert.Equal(created.Description, persisted.Description);
            Assert.Equal(created.Icon, persisted.Icon);
            Assert.Equal(created.Status, persisted.Status);
            Assert.Equal(created.SortOrder, persisted.SortOrder);
        }
    }

    private HttpClient CreateClient(string role, bool passwordChangeRequired = false)
    {
        return CreateClient(factory!, role, passwordChangeRequired);
    }

    private static HttpClient CreateClient(
        WebApplicationFactory<ControlPlaneApiMarker> clientFactory,
        string role,
        bool passwordChangeRequired = false)
    {
        var client = clientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwtTokenFactory.Issue(role: role, passwordChangeRequired: passwordChangeRequired));
        return client;
    }

    private static object ValidCreate(string code) => new { name = "Valid direction", code, description = "Description", icon = "icon", status = "Active", sortOrder = 10 };

    private static async Task<DirectionDto> CreateAsync(HttpClient client, string name, string code, int sortOrder)
    {
        using var response = await client.PostAsJsonAsync("/v1/directions", new { name, code, description = "Description", icon = "icon", status = "Active", sortOrder });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DirectionDto>(WebJson))!;
    }

    private static async Task AssertDirectionUnchangedAsync(HttpClient client, DirectionDto expected)
    {
        var actual = await client.GetFromJsonAsync<DirectionDto>($"/v1/directions/{expected.Id}", WebJson);
        Assert.NotNull(actual);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Code, actual.Code);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Icon, actual.Icon);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.SortOrder, actual.SortOrder);
        Assert.Equal(expected.Version, actual.Version);
    }

    private ControlPlaneDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
        .UseNpgsql(postgres.GetConnectionString(), npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "control_plane"))
        .Options);

    private async Task ClearDirectionsAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE TABLE control_plane.directions", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedDirectionsAsync(int count)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < count; index++)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO control_plane.directions "
                + "(id,name,code,status,sort_order,created_at,updated_at) "
                + "VALUES (@id,@name,@code,'Active',@sortOrder,now(),now())",
                connection,
                transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("name", $"Direction {index % 5:D2}");
            command.Parameters.AddWithValue("code", $"direction-{index:D2}");
            command.Parameters.AddWithValue("sortOrder", index % 3);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private WebApplicationFactory<ControlPlaneApiMarker> CreateCoordinatedFactory()
    {
        var barrier = new SaveBarrier(participantCount: 2);
        return factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var unitOfWork = Assert.Single(
                services,
                descriptor => descriptor.ServiceType == typeof(IControlPlaneUnitOfWork));
            Assert.Equal("ControlPlaneUnitOfWork", unitOfWork.ImplementationType?.Name);
            services.AddSingleton(barrier);
            services.AddSingleton<CoordinatedSaveChangesInterceptor>();
            services.AddDbContext<ControlPlaneDbContext>((provider, options) =>
                options.AddInterceptors(provider.GetRequiredService<CoordinatedSaveChangesInterceptor>()));
        }));
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private async Task<string[]> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private sealed class SaveBarrier(int participantCount)
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrivals) == participantCount)
            {
                release.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {participantCount} SaveChanges participants; observed {Volatile.Read(ref arrivals)}.",
                    exception);
            }
        }
    }

    private sealed class CoordinatedSaveChangesInterceptor(SaveBarrier barrier) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await barrier.SignalAndWaitAsync(cancellationToken);
            return result;
        }
    }
}
