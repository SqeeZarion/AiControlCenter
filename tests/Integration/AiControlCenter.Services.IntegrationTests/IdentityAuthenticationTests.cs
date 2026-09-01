using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AiControlCenter.Identity.Api;
using AiControlCenter.Identity.Domain;
using AiControlCenter.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace AiControlCenter.Services.IntegrationTests;

[Collection(IdentityAuthenticationTestGroup.Name)]
public sealed class IdentityAuthenticationTests : IAsyncLifetime
{
    private const string BootstrapEmail = "bootstrap-admin@example.test";
    private const string BootstrapPassword = "Development-Only-Password-123!";
    private const string JwtIssuer = "AiControlCenter.Identity";
    private const string JwtAudience = "aicontrolcenter-api";
    private const string JwtKeyId = "identity-dev-2026-01";
    private static readonly string[] StandardUserRoles = ["User"];
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("identity_auth_tests")
        .WithUsername("postgres")
        .WithPassword("test-superuser-password")
        .Build();
    private readonly string dataProtectionPath = Path.Combine(
        Path.GetTempPath(),
        $"aicontrolcenter-identity-auth-dp-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper output;
    private WebApplicationFactory<IdentityApiMarker>? factory;

    public IdentityAuthenticationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

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
                .UseSetting("JwtSigning:PrivateKeyPath", TestJwtTokenFactory.PrivateKeyPath)
                .UseSetting("DataProtection:KeysPath", dataProtectionPath)
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
        if (Directory.Exists(dataProtectionPath))
        {
            Directory.Delete(dataProtectionPath, recursive: true);
        }
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

        using var invalidatedReplacementRequest = CreateRefreshRequest(
            antiforgeryCookie,
            xsrfCookie,
            xsrfValue,
            rotatedRefreshCookie);
        using var invalidatedReplacement = await client.SendAsync(invalidatedReplacementRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidatedReplacement.StatusCode);

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
    public async Task RefreshAndLogoutAreSerializedOnTheStableSessionRowInBothOrders()
    {
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var csrfCookies = await GetCsrfCookiesAsync(client);

        using var loginA = await LoginAsync(client, BootstrapPassword);
        var tokenA = ReadCookie(loginA, "aicontrolcenter.refresh");
        using var refreshA = await client.SendAsync(CreateRefreshRequest(csrfCookies, tokenA));
        refreshA.EnsureSuccessStatusCode();
        var tokenB = ReadCookie(refreshA, "aicontrolcenter.refresh");

        using var logoutA = await client.SendAsync(CreateLogoutRequest(csrfCookies, tokenA));
        Assert.Equal(HttpStatusCode.NoContent, logoutA.StatusCode);
        using var refreshB = await client.SendAsync(CreateRefreshRequest(csrfCookies, tokenB));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshB.StatusCode);

        using var repeatedLogoutA = await client.SendAsync(CreateLogoutRequest(csrfCookies, tokenA));
        Assert.Equal(HttpStatusCode.NoContent, repeatedLogoutA.StatusCode);

        using var loginC = await LoginAsync(client, BootstrapPassword);
        var tokenC = ReadCookie(loginC, "aicontrolcenter.refresh");
        using var logoutC = await client.SendAsync(CreateLogoutRequest(csrfCookies, tokenC));
        Assert.Equal(HttpStatusCode.NoContent, logoutC.StatusCode);
        using var refreshC = await client.SendAsync(CreateRefreshRequest(csrfCookies, tokenC));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshC.StatusCode);
    }

    [Theory]
    [InlineData(RefreshConcurrencyCase.RefreshVersusRefresh)]
    [InlineData(RefreshConcurrencyCase.RefreshVersusLogout)]
    [InlineData(RefreshConcurrencyCase.LogoutVersusRefresh)]
    [InlineData(RefreshConcurrencyCase.ReplayVersusReplacementRefresh)]
    public async Task RefreshSessionOperationsOverlapAtTheSameForUpdateLock(
        RefreshConcurrencyCase concurrencyCase)
    {
        var barrier = new RefreshSessionLockBarrier();
        await using var barrierFactory = CreateFactoryWithInterceptor(barrier);
        using var client = barrierFactory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        var csrfCookies = await GetCsrfCookiesAsync(client);
        using var login = await LoginAsync(client, BootstrapPassword);
        login.EnsureSuccessStatusCode();
        var originalToken = ReadCookie(login, "aicontrolcenter.refresh");
        var replacementToken = originalToken;
        if (concurrencyCase == RefreshConcurrencyCase.ReplayVersusReplacementRefresh)
        {
            using var initialRotation = await client.SendAsync(
                CreateRefreshRequest(csrfCookies, originalToken));
            initialRotation.EnsureSuccessStatusCode();
            replacementToken = ReadCookie(initialRotation, "aicontrolcenter.refresh");
        }

        barrier.Arm();
        using var firstRequest = concurrencyCase switch
        {
            RefreshConcurrencyCase.RefreshVersusRefresh => CreateRefreshRequest(csrfCookies, originalToken),
            RefreshConcurrencyCase.RefreshVersusLogout => CreateRefreshRequest(csrfCookies, originalToken),
            RefreshConcurrencyCase.LogoutVersusRefresh => CreateLogoutRequest(csrfCookies, originalToken),
            RefreshConcurrencyCase.ReplayVersusReplacementRefresh => CreateRefreshRequest(csrfCookies, originalToken),
            _ => throw new ArgumentOutOfRangeException(nameof(concurrencyCase)),
        };
        using var secondRequest = concurrencyCase switch
        {
            RefreshConcurrencyCase.RefreshVersusRefresh => CreateRefreshRequest(csrfCookies, originalToken),
            RefreshConcurrencyCase.RefreshVersusLogout => CreateLogoutRequest(csrfCookies, originalToken),
            RefreshConcurrencyCase.LogoutVersusRefresh => CreateRefreshRequest(csrfCookies, originalToken),
            RefreshConcurrencyCase.ReplayVersusReplacementRefresh => CreateRefreshRequest(csrfCookies, replacementToken),
            _ => throw new ArgumentOutOfRangeException(nameof(concurrencyCase)),
        };

        var firstTask = client.SendAsync(firstRequest);
        await barrier.FirstLockAcquired.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = client.SendAsync(secondRequest);
        try
        {
            await barrier.SecondCommandDispatched.WaitAsync(TimeSpan.FromSeconds(10));
            var lockObservation = await WaitForRefreshSessionLockAsync(
                barrier.FirstBackendPid!.Value,
                barrier.SecondBackendPid!.Value);
            output.WriteLine(
                "PostgreSQL row lock confirmed: first PID={0}, second PID={1}, state={2}, "
                + "wait_event_type={3}, wait_event={4}, blocking_pids=[{5}]",
                lockObservation.FirstPid,
                lockObservation.SecondPid,
                lockObservation.State,
                lockObservation.WaitEventType,
                lockObservation.WaitEvent,
                string.Join(',', lockObservation.BlockingPids));
            Assert.Equal("Lock", lockObservation.WaitEventType);
            Assert.Contains(lockObservation.FirstPid, lockObservation.BlockingPids);
        }
        finally
        {
            barrier.ReleaseFirst();
        }

        var responses = await Task.WhenAll(firstTask, secondTask);
        try
        {
            Assert.True(barrier.OverlapConfirmed);
            Assert.Equal(2, barrier.ForUpdateExecutionCount);
            Assert.NotNull(barrier.FirstSessionId);
            Assert.Equal(barrier.FirstSessionId, barrier.SecondSessionId);
            Assert.DoesNotContain(responses, response =>
                response.StatusCode == HttpStatusCode.InternalServerError);
            AssertConcurrencyOutcomes(concurrencyCase, responses);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var dbContext = CreateDbContext();
        Assert.Equal(0, await ExecuteScalarIntAsync(
            dbContext,
            """
            SELECT COUNT(*)
            FROM identity.refresh_tokens AS token
            INNER JOIN identity.refresh_sessions AS session ON session.id = token.session_id
            WHERE session.revoked_at IS NULL
              AND session.expires_at > now()
              AND token.used_at IS NULL
              AND token.replaced_by_token_id IS NULL
              AND token.expires_at > now()
            """));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(DISTINCT user_id) FROM identity.refresh_sessions"));
    }

    public enum RefreshConcurrencyCase
    {
        RefreshVersusRefresh,
        RefreshVersusLogout,
        LogoutVersusRefresh,
        ReplayVersusReplacementRefresh,
    }

    [Fact]
    public async Task ConcurrentFailedLoginsAreCountedAtomicallyWithoutServerErrors()
    {
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var startup = await client.GetAsync("/health/live");
        startup.EnsureSuccessStatusCode();
        await using var lockHolder = new NpgsqlConnection(postgres.GetConnectionString());
        await lockHolder.OpenAsync();
        await using (var acquire = new NpgsqlCommand(
            $"SELECT pg_advisory_lock({FailedLoginBarrierKey})",
            lockHolder))
        {
            await acquire.ExecuteNonQueryAsync();
        }

        await InstallFailedLoginBarrierAsync();
        HttpResponseMessage[]? responses = null;
        try
        {
            var attempts = Enumerable.Range(0, 10).Select(_ =>
                client.PostAsJsonAsync("/v1/auth/login", new
                {
                    email = BootstrapEmail,
                    password = "Definitely-Wrong-Password-123!",
                })).ToArray();

            var overlappedCount = await WaitForFailedLoginBarrierAsync(expectedCount: 10);
            Assert.Equal(10, overlappedCount);
            await ReleaseAdvisoryLockAsync(lockHolder, FailedLoginBarrierKey);
            responses = await Task.WhenAll(attempts);

            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        }
        finally
        {
            await ReleaseAdvisoryLockAsync(lockHolder, FailedLoginBarrierKey);
            await RemoveFailedLoginBarrierAsync();
            if (responses is not null)
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }

        await using var dbContext = CreateDbContext();
        var user = await dbContext.Users.AsNoTracking()
            .SingleAsync(value => value.Email == Email.Create(BootstrapEmail));
        Assert.Equal(5, user.AccessFailedCount);
        Assert.NotNull(user.LockoutEnd);
    }

    [Fact]
    public async Task PasswordAndLogoutDatabaseFailuresRollBackInsteadOfReturningFalseSuccess()
    {
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var csrfCookies = await GetCsrfCookiesAsync(client);
        using var login = await LoginAsync(client, BootstrapPassword);
        var refreshToken = ReadCookie(login, "aicontrolcenter.refresh");
        var accessToken = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

        await InstallRejectSessionUpdateTriggerAsync();
        try
        {
            using var changePassword = AuthorizedRequest(HttpMethod.Post, "/v1/users/me/change-password", accessToken);
            changePassword.Content = JsonContent.Create(new
            {
                currentPassword = BootstrapPassword,
                newPassword = "Password-That-Must-Roll-Back-456!",
            });
            using var failedChange = await client.SendAsync(changePassword);
            Assert.Equal(HttpStatusCode.InternalServerError, failedChange.StatusCode);
        }
        finally
        {
            await RemoveRejectSessionUpdateTriggerAsync();
        }

        using var oldPasswordLogin = await LoginAsync(client, BootstrapPassword);
        oldPasswordLogin.EnsureSuccessStatusCode();
        using var stillValidRefresh = await client.SendAsync(CreateRefreshRequest(csrfCookies, refreshToken));
        stillValidRefresh.EnsureSuccessStatusCode();
        var replacement = ReadCookie(stillValidRefresh, "aicontrolcenter.refresh");

        await InstallRejectSessionUpdateTriggerAsync();
        try
        {
            using var failedLogout = await client.SendAsync(CreateLogoutRequest(csrfCookies, replacement));
            Assert.Equal(HttpStatusCode.InternalServerError, failedLogout.StatusCode);
        }
        finally
        {
            await RemoveRejectSessionUpdateTriggerAsync();
        }

        using var refreshAfterFailedLogout = await client.SendAsync(CreateRefreshRequest(csrfCookies, replacement));
        refreshAfterFailedLogout.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ResetPasswordDatabaseFailureRollsBackPasswordAndSessionRevocation()
    {
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var csrfCookies = await GetCsrfCookiesAsync(client);
        using var initialAdminLogin = await LoginAsync(client, BootstrapPassword);
        initialAdminLogin.EnsureSuccessStatusCode();
        var initialAdminToken = JsonDocument.Parse(await initialAdminLogin.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;
        const string adminPassword = "Admin-Reset-Rollback-456!";
        using (var changePassword = AuthorizedRequest(
            HttpMethod.Post,
            "/v1/users/me/change-password",
            initialAdminToken))
        {
            changePassword.Content = JsonContent.Create(new
            {
                currentPassword = BootstrapPassword,
                newPassword = adminPassword,
            });
            using var changed = await client.SendAsync(changePassword);
            changed.EnsureSuccessStatusCode();
        }

        using var adminLogin = await LoginAsync(client, adminPassword);
        adminLogin.EnsureSuccessStatusCode();
        var adminToken = JsonDocument.Parse(await adminLogin.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;
        const string userPassword = "Temporary-Reset-Rollback-123!";
        using var createUser = AuthorizedRequest(HttpMethod.Post, "/v1/users", adminToken);
        createUser.Content = JsonContent.Create(new
        {
            email = "reset-rollback@example.test",
            displayName = "Reset Rollback",
            temporaryPassword = userPassword,
            roles = StandardUserRoles,
        });
        using var created = await client.SendAsync(createUser);
        created.EnsureSuccessStatusCode();
        var userId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var userLogin = await client.PostAsJsonAsync("/v1/auth/login", new
        {
            email = "reset-rollback@example.test",
            password = userPassword,
        });
        userLogin.EnsureSuccessStatusCode();
        var refreshToken = ReadCookie(userLogin, "aicontrolcenter.refresh");

        await InstallRejectSessionUpdateTriggerAsync();
        try
        {
            using var reset = AuthorizedRequest(
                HttpMethod.Post,
                $"/v1/users/{userId}/reset-password",
                adminToken);
            reset.Content = JsonContent.Create(new
            {
                temporaryPassword = "Replacement-That-Must-Roll-Back-789!",
            });
            using var failedReset = await client.SendAsync(reset);
            Assert.Equal(HttpStatusCode.InternalServerError, failedReset.StatusCode);
        }
        finally
        {
            await RemoveRejectSessionUpdateTriggerAsync();
        }

        using var oldPasswordLogin = await client.PostAsJsonAsync("/v1/auth/login", new
        {
            email = "reset-rollback@example.test",
            password = userPassword,
        });
        oldPasswordLogin.EnsureSuccessStatusCode();
        using var refreshAfterFailedReset = await client.SendAsync(
            CreateRefreshRequest(csrfCookies, refreshToken));
        refreshAfterFailedReset.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task IdentityUsesOneImmutableRsaSnapshotUntilRestart()
    {
        using (var bootstrapClient = factory!.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false }))
        using (var bootstrapLogin = await LoginAsync(bootstrapClient, BootstrapPassword))
        {
            bootstrapLogin.EnsureSuccessStatusCode();
        }

        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"aicontrolcenter-identity-snapshot-{Guid.NewGuid():N}");
        var publicKeyPath = Path.Combine(keyDirectory, "identity-public.pem");
        var privateKeyPath = Path.Combine(keyDirectory, "identity-private.pem");
        Directory.CreateDirectory(keyDirectory);
        try
        {
            var keyPairA = CreateKeyPair();
            var keyPairB = CreateKeyPair();
            WriteKeyPair(publicKeyPath, privateKeyPath, keyPairA);
            await using (var firstFactory = CreateFactoryWithKeyPaths(
                publicKeyPath,
                privateKeyPath,
                Path.Combine(keyDirectory, "data-protection-a")))
            {
                using var firstClient = firstFactory.CreateClient(
                    new WebApplicationFactoryClientOptions { HandleCookies = false });
                using var started = await firstClient.GetAsync("/health/live");
                started.EnsureSuccessStatusCode();

                WriteKeyPair(publicKeyPath, privateKeyPath, keyPairB);

                using var login = await LoginAsync(firstClient, BootstrapPassword);
                login.EnsureSuccessStatusCode();
                var accessToken = await ReadAccessTokenAsync(login);
                using var currentUser = AuthorizedRequest(HttpMethod.Get, "/v1/users/me", accessToken);
                using var currentUserResponse = await firstClient.SendAsync(currentUser);
                currentUserResponse.EnsureSuccessStatusCode();
            }

            await using var restartedFactory = CreateFactoryWithKeyPaths(
                publicKeyPath,
                privateKeyPath,
                Path.Combine(keyDirectory, "data-protection-b"));
            using var restartedClient = restartedFactory.CreateClient(
                new WebApplicationFactoryClientOptions { HandleCookies = false });
            using var restartedLogin = await LoginAsync(restartedClient, BootstrapPassword);
            restartedLogin.EnsureSuccessStatusCode();
            var restartedAccessToken = await ReadAccessTokenAsync(restartedLogin);
            using var restartedCurrentUser = AuthorizedRequest(
                HttpMethod.Get,
                "/v1/users/me",
                restartedAccessToken);
            using var restartedCurrentUserResponse = await restartedClient.SendAsync(restartedCurrentUser);
            restartedCurrentUserResponse.EnsureSuccessStatusCode();

            var validationWithB = await ValidateAccessTokenAsync(
                restartedAccessToken,
                keyPairB.PublicKeyPem);
            Assert.True(validationWithB.IsValid, validationWithB.Exception?.ToString());

            var validationWithA = await ValidateAccessTokenAsync(
                restartedAccessToken,
                keyPairA.PublicKeyPem);
            Assert.False(validationWithA.IsValid);
            Assert.IsType<SecurityTokenInvalidSignatureException>(validationWithA.Exception);
        }
        finally
        {
            Directory.Delete(keyDirectory, recursive: true);
        }
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
        Assert.Equal(5, await CountIdentityBusinessTablesAsync(dbContext));
    }

    [Fact]
    public async Task RefreshTokenHistorySurvivesForwardMigrationAndDownMigration()
    {
        await using var dbContext = CreateDbContext();
        await ApplyRefreshSessionsDownAsync(dbContext);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var revokedFamilyId = Guid.NewGuid();
        var tokenAId = Guid.NewGuid();
        var tokenBId = Guid.NewGuid();
        var tokenCId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
        var rotatedAt = createdAt.AddHours(1);
        var revokedAt = createdAt.AddHours(2);
        var expiresAt = createdAt.AddDays(30);
        var tokenAHash = new string('a', 64);
        var tokenBHash = new string('b', 64);
        var tokenCHash = new string('c', 64);
        var migratedEmail = $"m{Guid.NewGuid():N}@example.test";
        const string migratedDisplayName = "M";
        const string migratedPasswordHash = "hash";
        const string activeStatus = "Active";
        const string rotatedReason = "Rotated";
        const string revokedReason = "Password reset by Admin";
        DateTimeOffset? noTimestamp = null;
        string? noReason = null;
        Guid? noReplacement = null;

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO identity.users
                (id, normalized_email, display_name, password_hash, status, must_change_password,
                 access_failed_count, created_at, updated_at)
            VALUES
                ({userId}, {migratedEmail}, {migratedDisplayName}, {migratedPasswordHash},
                 {activeStatus}, {false}, {0}, {createdAt}, {createdAt});

            INSERT INTO identity.refresh_tokens
                (id, user_id, token_hash, family_id, created_at, expires_at, revoked_at,
                 revocation_reason, replaced_by_token_id)
            VALUES
                ({tokenAId}, {userId}, {tokenAHash}, {familyId}, {createdAt}, {expiresAt},
                 {rotatedAt}, {rotatedReason}, {tokenBId}),
                ({tokenBId}, {userId}, {tokenBHash}, {familyId}, {rotatedAt}, {expiresAt},
                 {noTimestamp}, {noReason}, {noReplacement}),
                ({tokenCId}, {userId}, {tokenCHash}, {revokedFamilyId}, {createdAt}, {expiresAt},
                 {revokedAt}, {revokedReason}, {noReplacement});
            """);

        await dbContext.Database.MigrateAsync();
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_sessions WHERE id = @id AND revoked_at IS NULL",
            ("id", familyId)));
        Assert.Equal(2, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_tokens WHERE session_id = @id",
            ("id", familyId)));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_tokens WHERE id = @id AND used_at IS NOT NULL",
            ("id", tokenAId)));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_sessions WHERE id = @id AND revoked_at = @revoked_at AND revoked_reason = @reason",
            ("id", revokedFamilyId),
            ("revoked_at", revokedAt),
            ("reason", revokedReason)));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_tokens WHERE id = @id AND token_hash = @hash AND replaced_by_token_id = @replacement",
            ("id", tokenAId),
            ("hash", tokenAHash),
            ("replacement", tokenBId)));

        await ApplyRefreshSessionsDownAsync(dbContext);
        Assert.Equal(2, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_tokens WHERE family_id = @id AND user_id = @user_id",
            ("id", familyId),
            ("user_id", userId)));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_tokens WHERE id = @id AND revoked_at IS NULL",
            ("id", tokenBId)));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            """
            SELECT COUNT(*) FROM identity.refresh_tokens
            WHERE id = @id
              AND token_hash = @hash
              AND family_id = @family_id
              AND revoked_at = @rotated_at
              AND revocation_reason = @reason
              AND replaced_by_token_id = @replacement
            """,
            ("id", tokenAId),
            ("hash", tokenAHash),
            ("family_id", familyId),
            ("rotated_at", rotatedAt),
            ("reason", rotatedReason),
            ("replacement", tokenBId)));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            """
            SELECT COUNT(*) FROM identity.refresh_tokens
            WHERE id = @id
              AND token_hash = @hash
              AND family_id = @family_id
              AND revoked_at = @revoked_at
              AND revocation_reason = @reason
              AND replaced_by_token_id IS NULL
            """,
            ("id", tokenCId),
            ("hash", tokenCHash),
            ("family_id", revokedFamilyId),
            ("revoked_at", revokedAt),
            ("reason", revokedReason)));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsCrossUserFamilyBeforeMutation()
    {
        await using var dbContext = CreateDbContext();
        await ApplyRefreshSessionsDownAsync(dbContext);
        var familyId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var firstUserId = await InsertLegacyUserAsync(dbContext, now);
        var secondUserId = await InsertLegacyUserAsync(dbContext, now);
        await InsertLegacyCurrentTokenAsync(dbContext, firstUserId, familyId, new string('c', 64), now);
        await InsertLegacyCurrentTokenAsync(dbContext, secondUserId, familyId, new string('d', 64), now);
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'identity'
              AND table_name = 'refresh_tokens'
              AND column_name = 'family_id'
              AND is_nullable = 'NO'
            """));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => dbContext.Database.MigrateAsync());

        Assert.Contains("multiple distinct users", exception.MessageText, StringComparison.OrdinalIgnoreCase);
        await AssertLegacyMigrationStateIsUnchangedAsync(dbContext, expectedTokenCount: 2);
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsReplacementWithoutRevocationTimestamp()
    {
        await using var dbContext = CreateDbContext();
        await ApplyRefreshSessionsDownAsync(dbContext);
        var familyId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = await InsertLegacyUserAsync(dbContext, now);
        var originalId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO identity.refresh_tokens
                (id, user_id, token_hash, family_id, created_at, expires_at, revoked_at,
                 revocation_reason, replaced_by_token_id)
            VALUES
                ({originalId}, {userId}, {new string('7', 64)}, {familyId}, {now}, {now.AddDays(30)},
                 {(DateTimeOffset?)null}, {(string?)null}, {replacementId}),
                ({replacementId}, {userId}, {new string('8', 64)}, {familyId}, {now.AddMinutes(1)},
                 {now.AddDays(30)}, {(DateTimeOffset?)null}, {(string?)null}, {(Guid?)null});
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => dbContext.Database.MigrateAsync());

        Assert.Contains("replacement link has no revocation timestamp", exception.MessageText, StringComparison.OrdinalIgnoreCase);
        await AssertLegacyMigrationStateIsUnchangedAsync(dbContext, expectedTokenCount: 2);
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsMultipleCurrentTokensBeforeMutation()
    {
        await using var dbContext = CreateDbContext();
        await ApplyRefreshSessionsDownAsync(dbContext);
        var familyId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = await InsertLegacyUserAsync(dbContext, now);
        await InsertLegacyCurrentTokenAsync(dbContext, userId, familyId, new string('e', 64), now);
        await InsertLegacyCurrentTokenAsync(dbContext, userId, familyId, new string('f', 64), now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => dbContext.Database.MigrateAsync());

        Assert.Contains("multiple current tokens", exception.MessageText, StringComparison.OrdinalIgnoreCase);
        await AssertLegacyMigrationStateIsUnchangedAsync(dbContext, expectedTokenCount: 2);
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsCrossFamilyReplacementForSameUserBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "different refresh families",
            LegacyToken(now, userId, Guid.NewGuid(), 'g', replacementId: replacementId),
            LegacyToken(now.AddMinutes(1), userId, Guid.NewGuid(), 'h', id: replacementId, isCurrent: true));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsCrossFamilyReplacementForDifferentUsersBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var replacementId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "different users",
            LegacyToken(now, Guid.NewGuid(), Guid.NewGuid(), 'i', replacementId: replacementId),
            LegacyToken(now.AddMinutes(1), Guid.NewGuid(), Guid.NewGuid(), 'j', id: replacementId, isCurrent: true));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsSelfReferenceBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var tokenId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "self-reference",
            LegacyToken(now, Guid.NewGuid(), Guid.NewGuid(), 'k', id: tokenId, replacementId: tokenId));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsTwoTokenCycleBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "cycle",
            LegacyToken(now, userId, familyId, 'l', id: firstId, replacementId: secondId),
            LegacyToken(now.AddMinutes(1), userId, familyId, 'm', id: secondId, replacementId: firstId));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsLongerCycleBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "cycle",
            LegacyToken(now, userId, familyId, 'n', id: firstId, replacementId: secondId),
            LegacyToken(now.AddMinutes(1), userId, familyId, 'o', id: secondId, replacementId: thirdId),
            LegacyToken(now.AddMinutes(2), userId, familyId, 'p', id: thirdId, replacementId: firstId));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsBranchingBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "branching",
            LegacyToken(now, userId, familyId, 'q', replacementId: terminalId),
            LegacyToken(now.AddMinutes(1), userId, familyId, 'r', replacementId: terminalId),
            LegacyToken(now.AddMinutes(2), userId, familyId, 's', id: terminalId, isCurrent: true));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsDisconnectedChainsBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var firstTerminalId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "exactly one terminal token",
            LegacyToken(now, userId, familyId, 't', replacementId: firstTerminalId),
            LegacyToken(now.AddMinutes(1), userId, familyId, 'u', id: firstTerminalId, isCurrent: true),
            LegacyToken(now.AddMinutes(2), userId, familyId, 'v', isCurrent: false, revocationReason: "Blocked"));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsMultipleTerminalRevokedTokensBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "exactly one terminal token",
            LegacyToken(now, userId, familyId, 'w', isCurrent: false, revocationReason: "Logout"),
            LegacyToken(now.AddMinutes(1), userId, familyId, 'x', isCurrent: false, revocationReason: "Blocked"));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsInconsistentExpirationBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        await AssertMigrationRejectsLegacyGraphAsync(
            "inconsistent expiration",
            LegacyToken(now, userId, familyId, 'y', replacementId: terminalId),
            LegacyToken(
                now.AddSeconds(30),
                userId,
                familyId,
                'z',
                id: terminalId,
                isCurrent: true,
                expiresAt: now.AddDays(31)));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsReplacementOutsideRotatedStateBeforeMutation()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var expiresAt = now.AddDays(30);
        await AssertMigrationRejectsLegacyGraphAsync(
            "Rotated state",
            LegacyToken(
                now,
                userId,
                familyId,
                '0',
                replacementId: terminalId,
                revocationReason: "Blocked",
                expiresAt: expiresAt),
            LegacyToken(
                now.AddSeconds(30),
                userId,
                familyId,
                '3',
                id: terminalId,
                isCurrent: true,
                expiresAt: expiresAt));
    }

    [Fact]
    public async Task RefreshSessionMigrationRejectsTerminalRevokedBeforeCreationBeforeMutation()
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
        await AssertMigrationRejectsLegacyGraphAsync(
            "inconsistent lifecycle state",
            LegacyToken(
                createdAt,
                Guid.NewGuid(),
                Guid.NewGuid(),
                '4',
                isCurrent: false,
                revocationReason: "User logout",
                revokedAt: createdAt.AddSeconds(-1)));
    }

    [Fact]
    public async Task RefreshSessionMigrationAcceptsTerminalRevokedAtCreation()
    {
        await using var dbContext = CreateDbContext();
        await ApplyRefreshSessionsDownAsync(dbContext);
        var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var rows = new[]
        {
            LegacyToken(
                createdAt,
                userId,
                familyId,
                '5',
                isCurrent: false,
                revocationReason: "User logout",
                revokedAt: createdAt),
        };
        await InsertLegacyGraphAsync(dbContext, rows);
        var before = await ReadLegacyTokenSnapshotAsync(dbContext);

        await dbContext.Database.MigrateAsync();

        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_sessions WHERE id = @id AND revoked_at = created_at",
            ("id", familyId)));
        await ApplyRefreshSessionsDownAsync(dbContext);
        Assert.Equal(before, await ReadLegacyTokenSnapshotAsync(dbContext));
    }

    [Fact]
    public async Task RefreshSessionMigrationAcceptsAndRoundTripsRevokedHistoricalLinearChain()
    {
        await using var dbContext = CreateDbContext();
        await ApplyRefreshSessionsDownAsync(dbContext);
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var expiresAt = now.AddDays(30);
        var rows = new[]
        {
            LegacyToken(now, userId, familyId, '1', replacementId: terminalId, expiresAt: expiresAt),
            LegacyToken(
                now.AddSeconds(30),
                userId,
                familyId,
                '2',
                id: terminalId,
                isCurrent: false,
                revocationReason: "User logout",
                expiresAt: expiresAt),
        };
        await InsertLegacyGraphAsync(dbContext, rows);
        var before = await ReadLegacyTokenSnapshotAsync(dbContext);

        await dbContext.Database.MigrateAsync();
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_sessions WHERE id = @id AND revoked_reason = @reason",
            ("id", familyId),
            ("reason", "User logout")));
        await ApplyRefreshSessionsDownAsync(dbContext);

        Assert.Equal(before, await ReadLegacyTokenSnapshotAsync(dbContext));
        await dbContext.Database.MigrateAsync();
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_sessions WHERE id = @id",
            ("id", familyId)));
    }

    private async Task AssertMigrationRejectsLegacyGraphAsync(
        string expectedReason,
        params LegacyTokenRow[] rows)
    {
        await using var dbContext = CreateDbContext();
        await ApplyRefreshSessionsDownAsync(dbContext);
        await InsertLegacyGraphAsync(dbContext, rows);
        var before = await ReadLegacyTokenSnapshotAsync(dbContext);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => dbContext.Database.MigrateAsync());

        Assert.Contains(expectedReason, exception.MessageText, StringComparison.OrdinalIgnoreCase);
        await AssertLegacyMigrationStateIsUnchangedAsync(dbContext, rows.Length);
        Assert.Equal(before, await ReadLegacyTokenSnapshotAsync(dbContext));
    }

    private static LegacyTokenRow LegacyToken(
        DateTimeOffset createdAt,
        Guid userId,
        Guid familyId,
        char hashCharacter,
        Guid? id = null,
        Guid? replacementId = null,
        bool isCurrent = false,
        string? revocationReason = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null) =>
        new(
            id ?? Guid.NewGuid(),
            userId,
            new string(hashCharacter, 64),
            familyId,
            createdAt,
            expiresAt ?? createdAt.AddDays(30),
            isCurrent ? null : revokedAt ?? createdAt.AddSeconds(30),
            isCurrent ? null : revocationReason ?? (replacementId is null ? "Revoked" : "Rotated"),
            replacementId);

    private static async Task InsertLegacyGraphAsync(
        IdentityDbContext dbContext,
        IReadOnlyCollection<LegacyTokenRow> rows)
    {
        foreach (var userId in rows.Select(row => row.UserId).Distinct())
        {
            var createdAt = rows.Where(row => row.UserId == userId).Min(row => row.CreatedAt);
            var email = $"legacy-{userId:N}@example.test";
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO identity.users
                    (id, normalized_email, display_name, password_hash, status, must_change_password,
                     access_failed_count, created_at, updated_at)
                VALUES
                    ({userId}, {email}, {"Legacy"}, {"hash"}, {"Active"}, {false}, {0}, {createdAt}, {createdAt});
                """);
        }

        foreach (var row in rows)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO identity.refresh_tokens
                    (id, user_id, token_hash, family_id, created_at, expires_at, revoked_at,
                     revocation_reason, replaced_by_token_id)
                VALUES
                    ({row.Id}, {row.UserId}, {row.TokenHash}, {row.FamilyId}, {row.CreatedAt},
                     {row.ExpiresAt}, {row.RevokedAt}, {row.RevocationReason}, {(Guid?)null});
                """);
        }

        foreach (var row in rows.Where(row => row.ReplacedByTokenId is not null))
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE identity.refresh_tokens
                SET replaced_by_token_id = {row.ReplacedByTokenId}
                WHERE id = {row.Id};
                """);
        }
    }

    private static async Task<IReadOnlyList<LegacyTokenRow>> ReadLegacyTokenSnapshotAsync(
        IdentityDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, user_id, token_hash, family_id, created_at, expires_at, revoked_at,
                   revocation_reason, replaced_by_token_id
            FROM identity.refresh_tokens
            ORDER BY id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<LegacyTokenRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new LegacyTokenRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8)));
        }

        return rows;
    }

    private IdentityDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;
        return new IdentityDbContext(options);
    }

    private WebApplicationFactory<IdentityApiMarker> CreateFactoryWithKeyPaths(
        string publicKeyPath,
        string privateKeyPath,
        string snapshotDataProtectionPath) =>
        new WebApplicationFactory<IdentityApiMarker>()
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .UseSetting("ConnectionStrings:IdentityDatabase", postgres.GetConnectionString())
                .UseSetting("Authentication:PublicKeyPath", publicKeyPath)
                .UseSetting("JwtSigning:PrivateKeyPath", privateKeyPath)
                .UseSetting("DataProtection:KeysPath", snapshotDataProtectionPath)
                .UseSetting("Security:AllowedOrigins:0", "http://localhost:8080")
                .UseSetting("IdentityBootstrap:Disabled", "true"));

    private WebApplicationFactory<IdentityApiMarker> CreateFactoryWithInterceptor(DbCommandInterceptor interceptor) =>
        new WebApplicationFactory<IdentityApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder
                    .UseEnvironment("Development")
                    .UseSetting("ConnectionStrings:IdentityDatabase", postgres.GetConnectionString())
                    .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                    .UseSetting("JwtSigning:PrivateKeyPath", TestJwtTokenFactory.PrivateKeyPath)
                    .UseSetting("DataProtection:KeysPath", dataProtectionPath)
                    .UseSetting("Security:AllowedOrigins:0", "http://localhost:8080")
                    .UseSetting("IdentityBootstrap:Email", BootstrapEmail)
                    .UseSetting("IdentityBootstrap:TemporaryPassword", BootstrapPassword)
                    .UseSetting("IdentityBootstrap:DisplayName", "Bootstrap Admin");
                builder.ConfigureServices(services => services.AddDbContext<IdentityDbContext>(options =>
                    options.AddInterceptors(interceptor)));
            });

    private static void AssertConcurrencyOutcomes(
        RefreshConcurrencyCase concurrencyCase,
        IReadOnlyCollection<HttpResponseMessage> responses)
    {
        var statuses = responses.Select(response => response.StatusCode).ToArray();
        switch (concurrencyCase)
        {
            case RefreshConcurrencyCase.RefreshVersusRefresh:
                Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.OK));
                Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Unauthorized));
                break;
            case RefreshConcurrencyCase.RefreshVersusLogout:
                Assert.Contains(HttpStatusCode.OK, statuses);
                Assert.Contains(HttpStatusCode.NoContent, statuses);
                break;
            case RefreshConcurrencyCase.LogoutVersusRefresh:
                Assert.Contains(HttpStatusCode.NoContent, statuses);
                Assert.Contains(HttpStatusCode.Unauthorized, statuses);
                break;
            case RefreshConcurrencyCase.ReplayVersusReplacementRefresh:
                Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Unauthorized, status));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(concurrencyCase));
        }
    }

    private static async Task<Guid> InsertLegacyUserAsync(
        IdentityDbContext dbContext,
        DateTimeOffset now)
    {
        var userId = Guid.NewGuid();
        var email = $"legacy-{userId:N}@example.test";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO identity.users
                (id, normalized_email, display_name, password_hash, status, must_change_password,
                 access_failed_count, created_at, updated_at)
            VALUES
                ({userId}, {email}, {"Legacy"}, {"hash"}, {"Active"}, {false}, {0}, {now}, {now});
            """);
        return userId;
    }

    private static Task<int> InsertLegacyCurrentTokenAsync(
        IdentityDbContext dbContext,
        Guid userId,
        Guid familyId,
        string tokenHash,
        DateTimeOffset now) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO identity.refresh_tokens
                (id, user_id, token_hash, family_id, created_at, expires_at, revoked_at,
                 revocation_reason, replaced_by_token_id)
            VALUES
                ({Guid.NewGuid()}, {userId}, {tokenHash}, {familyId}, {now}, {now.AddDays(30)},
                 {(DateTimeOffset?)null}, {(string?)null}, {(Guid?)null});
            """);

    private static async Task AssertLegacyMigrationStateIsUnchangedAsync(
        IdentityDbContext dbContext,
        int expectedTokenCount)
    {
        Assert.Equal(expectedTokenCount, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM identity.refresh_tokens"));
        Assert.Equal(0, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'identity' AND table_name = 'refresh_sessions'"));
        Assert.Equal(1, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'identity' AND table_name = 'refresh_tokens' AND column_name = 'family_id'"));
        Assert.Equal(0, await ExecuteScalarIntAsync(
            dbContext,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'identity' AND table_name = 'refresh_tokens' AND column_name = 'session_id'"));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password) =>
        client.PostAsJsonAsync("/v1/auth/login", new { email = BootstrapEmail, password });

    private static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static RsaTestKeyPair CreateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return new RsaTestKeyPair(
            rsa.ExportSubjectPublicKeyInfoPem(),
            rsa.ExportPkcs8PrivateKeyPem());
    }

    private static void WriteKeyPair(
        string publicKeyPath,
        string privateKeyPath,
        RsaTestKeyPair keyPair)
    {
        File.WriteAllText(publicKeyPath, keyPair.PublicKeyPem);
        File.WriteAllText(privateKeyPath, keyPair.PrivateKeyPem);
    }

    private static async Task<TokenValidationResult> ValidateAccessTokenAsync(
        string accessToken,
        string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        var securityKey = new RsaSecurityKey(rsa)
        {
            KeyId = JwtKeyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        return await new JsonWebTokenHandler().ValidateTokenAsync(
            accessToken,
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                TryAllIssuerSigningKeys = false,
                ValidateIssuer = true,
                ValidIssuer = JwtIssuer,
                ValidateAudience = true,
                ValidAudience = JwtAudience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            });
    }

    private static async Task<(string Antiforgery, string Xsrf, string XsrfValue)> GetCsrfCookiesAsync(
        HttpClient client)
    {
        using var csrf = await client.GetAsync("/v1/auth/csrf");
        csrf.EnsureSuccessStatusCode();
        var xsrf = ReadCookie(csrf, "XSRF-TOKEN");
        return (
            ReadCookie(csrf, ".AiControlCenter.Antiforgery"),
            xsrf,
            Uri.UnescapeDataString(xsrf.Split('=', 2)[1]));
    }

    private static HttpRequestMessage CreateRefreshRequest(
        (string Antiforgery, string Xsrf, string XsrfValue) csrf,
        string refreshToken) =>
        CreateCookieOperationRequest("/v1/auth/refresh", csrf, refreshToken);

    private static HttpRequestMessage CreateRefreshRequest(
        string antiforgery,
        string xsrf,
        string xsrfValue,
        string refreshToken) =>
        CreateRefreshRequest((antiforgery, xsrf, xsrfValue), refreshToken);

    private static HttpRequestMessage CreateLogoutRequest(
        (string Antiforgery, string Xsrf, string XsrfValue) csrf,
        string refreshToken) =>
        CreateCookieOperationRequest("/v1/auth/logout", csrf, refreshToken);

    private static HttpRequestMessage CreateCookieOperationRequest(
        string path,
        (string Antiforgery, string Xsrf, string XsrfValue) csrf,
        string refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"{csrf.Antiforgery}; {csrf.Xsrf}; {refreshToken}");
        request.Headers.Add("Origin", "http://localhost:8080");
        request.Headers.Add("X-XSRF-TOKEN", csrf.XsrfValue);
        return request;
    }

    private async Task InstallRejectSessionUpdateTriggerAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION identity.reject_refresh_session_update()
            RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN
                RAISE EXCEPTION 'controlled refresh session update failure';
            END;
            $function$;
            CREATE TRIGGER reject_refresh_session_update
            BEFORE UPDATE ON identity.refresh_sessions
            FOR EACH ROW EXECUTE FUNCTION identity.reject_refresh_session_update();
            """);
    }

    private async Task RemoveRejectSessionUpdateTriggerAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP TRIGGER IF EXISTS reject_refresh_session_update ON identity.refresh_sessions;
            DROP FUNCTION IF EXISTS identity.reject_refresh_session_update();
            """);
    }

    private const long FailedLoginBarrierKey = 8_240_202_601;

    private async Task InstallFailedLoginBarrierAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.ExecuteSqlRawAsync(
            $"""
            CREATE OR REPLACE FUNCTION identity.wait_at_failed_login_barrier()
            RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN
                PERFORM pg_advisory_xact_lock({FailedLoginBarrierKey});
                RETURN NULL;
            END;
            $function$;
            CREATE TRIGGER wait_at_failed_login_barrier
            BEFORE UPDATE ON identity.users
            FOR EACH STATEMENT EXECUTE FUNCTION identity.wait_at_failed_login_barrier();
            """);
    }

    private async Task RemoveFailedLoginBarrierAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP TRIGGER IF EXISTS wait_at_failed_login_barrier ON identity.users;
            DROP FUNCTION IF EXISTS identity.wait_at_failed_login_barrier();
            """);
    }

    private async Task<int> WaitForFailedLoginBarrierAsync(int expectedCount)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND wait_event = 'advisory'
                  AND query LIKE 'UPDATE identity.users%'
                """,
                connection);
            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(timeout.Token),
                System.Globalization.CultureInfo.InvariantCulture);
            if (count >= expectedCount)
            {
                return count;
            }

            await Task.Yield();
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    private static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection, long key)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({key})", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<PostgreSqlLockObservation> WaitForRefreshSessionLockAsync(
        int firstPid,
        int secondPid)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        PostgreSqlLockObservation? lastObservation = null;
        while (!timeout.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT state, wait_event_type, wait_event, pg_blocking_pids(pid)
                FROM pg_stat_activity
                WHERE pid = @second_pid
                """,
                connection);
            command.Parameters.AddWithValue("second_pid", secondPid);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            if (await reader.ReadAsync(timeout.Token))
            {
                lastObservation = new PostgreSqlLockObservation(
                    firstPid,
                    secondPid,
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetFieldValue<int[]>(3));
                if (string.Equals(lastObservation.WaitEventType, "Lock", StringComparison.Ordinal)
                    && lastObservation.BlockingPids.Contains(firstPid))
                {
                    return lastObservation;
                }
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            $"PostgreSQL lock wait was not observed. First PID={firstPid}; second PID={secondPid}; "
            + $"last state={lastObservation?.State ?? "missing"}; "
            + $"wait_event_type={lastObservation?.WaitEventType ?? "null"}; "
            + $"wait_event={lastObservation?.WaitEvent ?? "null"}; "
            + $"blocking_pids=[{string.Join(',', lastObservation?.BlockingPids ?? [])}].");
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
              AND table_name IN ('users', 'roles', 'user_roles', 'refresh_tokens', 'refresh_sessions');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteScalarIntAsync(
        IdentityDbContext dbContext,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyRefreshSessionsDownAsync(IdentityDbContext dbContext)
    {
        var operations = new TestableAddRefreshSessionsMigration().BuildDownOperations();
        var sqlGenerator = dbContext.GetService<IMigrationsSqlGenerator>();
        foreach (var command in sqlGenerator.Generate(operations, dbContext.Model))
        {
            await dbContext.Database.ExecuteSqlRawAsync(command.CommandText);
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM identity.\"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260823114857_AddRefreshSessions'");
    }

    private sealed class TestableAddRefreshSessionsMigration
        : AiControlCenter.Identity.Infrastructure.Persistence.Migrations.AddRefreshSessions
    {
        public List<MigrationOperation> BuildDownOperations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Down(builder);
            return builder.Operations;
        }
    }

    private sealed class RefreshSessionLockBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource firstLockAcquired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource secondCommandDispatched =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int executionCount;
        private int firstReaderObserved;
        private volatile bool armed;

        public Task FirstLockAcquired => firstLockAcquired.Task;

        public Task SecondCommandDispatched => secondCommandDispatched.Task;

        public int ForUpdateExecutionCount => Volatile.Read(ref executionCount);

        public Guid? FirstSessionId { get; private set; }

        public Guid? SecondSessionId { get; private set; }

        public int? FirstBackendPid { get; private set; }

        public int? SecondBackendPid { get; private set; }

        public bool OverlapConfirmed { get; private set; }

        public void Arm() => armed = true;

        public void ReleaseFirst() => releaseFirst.TrySetResult();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!armed || !IsRefreshSessionForUpdate(command.CommandText))
            {
                return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }

            var ordinal = Interlocked.Increment(ref executionCount);
            var sessionId = command.Parameters.Cast<DbParameter>()
                .Select(parameter => parameter.Value)
                .OfType<Guid>()
                .SingleOrDefault();
            if (ordinal == 1)
            {
                FirstSessionId = sessionId;
                FirstBackendPid = ((NpgsqlConnection)command.Connection!).ProcessID;
            }
            else if (ordinal == 2)
            {
                SecondSessionId = sessionId;
                SecondBackendPid = ((NpgsqlConnection)command.Connection!).ProcessID;
                OverlapConfirmed = true;
                secondCommandDispatched.TrySetResult();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (armed
                && IsRefreshSessionForUpdate(command.CommandText)
                && Interlocked.CompareExchange(ref firstReaderObserved, 1, 0) == 0)
            {
                firstLockAcquired.TrySetResult();
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private static bool IsRefreshSessionForUpdate(string commandText) =>
            commandText.Contains("refresh_sessions", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record LegacyTokenRow(
        Guid Id,
        Guid UserId,
        string TokenHash,
        Guid FamilyId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt,
        string? RevocationReason,
        Guid? ReplacedByTokenId);

    private sealed record RsaTestKeyPair(string PublicKeyPem, string PrivateKeyPem);

    private sealed record PostgreSqlLockObservation(
        int FirstPid,
        int SecondPid,
        string? State,
        string? WaitEventType,
        string? WaitEvent,
        int[] BlockingPids);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityAuthenticationTestGroup
{
    public const string Name = "Identity authentication database tests";
}
