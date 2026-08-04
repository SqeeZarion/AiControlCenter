using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiControlCenter.Identity.Api;
using AiControlCenter.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class IdentityAuthenticationTests : IAsyncLifetime
{
    private const string BootstrapEmail = "bootstrap-admin@example.test";
    private const string BootstrapPassword = "Development-Only-Password-123!";
    private static readonly string[] StandardUserRoles = ["User"];
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("identity_auth_tests")
        .WithUsername("postgres")
        .WithPassword("test-superuser-password")
        .Build();
    private WebApplicationFactory<IdentityApiMarker>? factory;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;
        await using (var dbContext = new IdentityDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        factory = new WebApplicationFactory<IdentityApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .UseSetting("ConnectionStrings:IdentityDatabase", postgres.GetConnectionString())
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("JwtIssuer:PrivateKeyPath", TestJwtTokenFactory.PrivateKeyPath)
                .UseSetting("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), "aicontrolcenter-identity-auth-dp"))
                .UseSetting("Security:AllowedOrigins:0", "http://localhost:8080")
                .UseSetting("IdentityBootstrap:Email", BootstrapEmail)
                .UseSetting("IdentityBootstrap:TemporaryPassword", BootstrapPassword)
                .UseSetting("IdentityBootstrap:DisplayName", "Bootstrap Admin"));
    }

    public async Task DisposeAsync()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    [Fact]
    public async Task LoginRefreshPasswordChangeAdminAndLogoutFlowIsProtected()
    {
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var unknownLogin = await client.PostAsJsonAsync("/v1/auth/login", new
        {
            email = "missing@example.test",
            password = BootstrapPassword,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unknownLogin.StatusCode);

        using var csrf = await client.GetAsync("/v1/auth/csrf");
        csrf.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        var xsrfCookie = ReadCookie(csrf, "XSRF-TOKEN");
        var antiforgeryCookie = ReadCookie(csrf, ".AiControlCenter.Antiforgery");
        var antiforgerySetCookie = csrf.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".AiControlCenter.Antiforgery=", StringComparison.Ordinal));
        Assert.Contains("httponly", antiforgerySetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", antiforgerySetCookie, StringComparison.OrdinalIgnoreCase);
        var xsrfValue = xsrfCookie.Split('=', 2)[1];

        using var login = await client.PostAsJsonAsync("/v1/auth/login", new
        {
            email = BootstrapEmail,
            password = BootstrapPassword,
        });
        login.EnsureSuccessStatusCode();
        var originalRefreshCookie = ReadCookie(login, "aicontrolcenter.refresh");
        var setCookie = login.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("aicontrolcenter.refresh=", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/identity/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);

        var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var firstAccessToken = loginDocument.RootElement.GetProperty("accessToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(firstAccessToken));
        Assert.DoesNotContain("refreshToken", loginDocument.RootElement.EnumerateObject().Select(property => property.Name));

        using var forbiddenAdmin = AuthorizedRequest(HttpMethod.Get, "/v1/users", firstAccessToken);
        using var forbiddenAdminResponse = await client.SendAsync(forbiddenAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAdminResponse.StatusCode);

        using var missingCsrfRefresh = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        missingCsrfRefresh.Headers.Add("Cookie", originalRefreshCookie);
        missingCsrfRefresh.Headers.Add("Origin", "http://localhost:8080");
        using var missingCsrfResponse = await client.SendAsync(missingCsrfRefresh);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"{antiforgeryCookie}; {xsrfCookie}; {originalRefreshCookie}");
        refreshRequest.Headers.Add("Origin", "http://localhost:8080");
        refreshRequest.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrfValue));
        using var refresh = await client.SendAsync(refreshRequest);
        refresh.EnsureSuccessStatusCode();
        var rotatedRefreshCookie = ReadCookie(refresh, "aicontrolcenter.refresh");
        Assert.NotEqual(originalRefreshCookie, rotatedRefreshCookie);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", $"{antiforgeryCookie}; {xsrfCookie}; {originalRefreshCookie}");
        replayRequest.Headers.Add("Origin", "http://localhost:8080");
        replayRequest.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrfValue));
        using var replay = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        const string newPassword = "Changed-Development-Password-456!";
        using var changePassword = AuthorizedRequest(HttpMethod.Post, "/v1/users/me/change-password", firstAccessToken);
        changePassword.Content = JsonContent.Create(new
        {
            currentPassword = BootstrapPassword,
            newPassword,
        });
        using var changed = await client.SendAsync(changePassword);
        changed.EnsureSuccessStatusCode();

        using var secondLogin = await client.PostAsJsonAsync("/v1/auth/login", new
        {
            email = BootstrapEmail,
            password = newPassword,
        });
        secondLogin.EnsureSuccessStatusCode();
        var secondDocument = JsonDocument.Parse(await secondLogin.Content.ReadAsStringAsync());
        var adminAccessToken = secondDocument.RootElement.GetProperty("accessToken").GetString()!;
        var activeRefreshCookie = ReadCookie(secondLogin, "aicontrolcenter.refresh");

        using var listUsers = AuthorizedRequest(HttpMethod.Get, "/v1/users", adminAccessToken);
        using var listUsersResponse = await client.SendAsync(listUsers);
        listUsersResponse.EnsureSuccessStatusCode();

        using var createUser = AuthorizedRequest(HttpMethod.Post, "/v1/users", adminAccessToken);
        createUser.Content = JsonContent.Create(new
        {
            email = "user@example.test",
            displayName = "Standard User",
            temporaryPassword = "Temporary-User-Password-123!",
            roles = StandardUserRoles,
        });
        using var createdUser = await client.SendAsync(createUser);
        Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/logout");
        logout.Headers.Add("Cookie", $"{antiforgeryCookie}; {xsrfCookie}; {activeRefreshCookie}");
        logout.Headers.Add("Origin", "http://localhost:8080");
        logout.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrfValue));
        using var loggedOut = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
    }

    [Fact]
    public async Task IdentityMigrationsCanRollBackAndReapplyFromZero()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        await using var dbContext = new IdentityDbContext(options);
        await dbContext.Database.MigrateAsync("0");
        Assert.Equal(0, await CountIdentityBusinessTablesAsync(dbContext));

        await dbContext.Database.MigrateAsync();
        Assert.Equal(4, await CountIdentityBusinessTablesAsync(dbContext));
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string ReadCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith($"{name}=", StringComparison.Ordinal));
        return header.Split(';', 2)[0];
    }

    private static async Task<int> CountIdentityBusinessTablesAsync(IdentityDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'identity'
              AND table_name IN ('users', 'roles', 'user_roles', 'refresh_tokens');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
