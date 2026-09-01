using System.Net;
using System.Security.Cryptography;
using AiControlCenter.Identity.Api;
using AiControlCenter.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Services.IntegrationTests;

[Collection(IdentityJwtStartupTestGroup.Name)]
public sealed class IdentityJwtStartupTests
{
    private const int MaximumPemSizeBytes = 64 * 1024;

    [Fact]
    public async Task RealDevelopmentConfigurationBindsCanonicalSigningSectionAndStartsHealthy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Services",
            "Identity",
            "AiControlCenter.Identity.Api");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(sourceRoot)
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json")
            .Build();

        Assert.Equal(
            "secrets/identity/identity-private.pem",
            configuration[$"{JwtSigningOptions.SectionName}:PrivateKeyPath"]);
        Assert.Null(configuration["JwtIssuer:PrivateKeyPath"]);

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"aicontrolcenter-development-config-{Guid.NewGuid():N}");
        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "secrets", "identity"));
        File.Copy(Path.Combine(sourceRoot, "appsettings.json"), Path.Combine(temporaryRoot, "appsettings.json"));
        File.Copy(
            Path.Combine(sourceRoot, "appsettings.Development.json"),
            Path.Combine(temporaryRoot, "appsettings.Development.json"));
        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "secrets", "identity", "identity-public.pem"),
                rsa.ExportSubjectPublicKeyInfoPem());
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "secrets", "identity", "identity-private.pem"),
                rsa.ExportPkcs8PrivateKeyPem());
        }

        try
        {
            Directory.SetCurrentDirectory(temporaryRoot);
            await using var factory = new WebApplicationFactory<IdentityApiMarker>()
                .WithWebHostBuilder(builder => builder
                    .UseContentRoot(temporaryRoot)
                    .UseEnvironment("Development")
                    .UseSetting(
                        "ConnectionStrings:IdentityDatabase",
                        "Host=127.0.0.1;Database=unused;Username=unused;Password=unused")
                    .UseSetting("IdentityBootstrap:Disabled", "true"));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
    [Fact]
    public async Task ValidKeyPairAndCanonicalConfigurationStartIdentity()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PrivatePublicKeyPaths))]
    public async Task PrivatePemInPublicKeyPathFailsIdentityStartup(string publicKeyPath)
    {
        await using var factory = CreateFactory(("Authentication:PublicKeyPath", publicKeyPath));

        await AssertStartupFailsAsync<JwtIssuerOptions>(
            factory,
            "Authentication:PublicKeyPath contains private key material");
    }

    [Fact]
    public Task MalformedPublicKeyFailsIdentityStartup() =>
        WithTemporaryFileAsync("not-an-rsa-key", async publicKeyPath =>
        {
            await using var factory = CreateFactory(("Authentication:PublicKeyPath", publicKeyPath));
            await AssertStartupFailsAsync<JwtIssuerOptions>(factory, "valid RSA public PEM key");
        });

    [Fact]
    public Task OversizedPublicKeyFailsIdentityStartupWithBoundedValidationError()
    {
        using var rsa = RSA.Create(2048);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var oversizedPem = publicPem + new string(' ', MaximumPemSizeBytes + 1 - publicPem.Length);
        return WithTemporaryFileAsync(oversizedPem, async publicKeyPath =>
        {
            await using var factory = CreateFactory(("Authentication:PublicKeyPath", publicKeyPath));
            await AssertStartupFailsAsync<JwtIssuerOptions>(factory, "maximum supported PEM size");
        });
    }

    [Fact]
    public Task OversizedPrivateKeyFailsIdentityStartupWithBoundedValidationError()
    {
        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();
        var oversizedPem = privatePem + new string(' ', MaximumPemSizeBytes + 1 - privatePem.Length);
        return WithTemporaryFileAsync(oversizedPem, async privateKeyPath =>
        {
            await using var factory = CreateFactory(("JwtSigning:PrivateKeyPath", privateKeyPath));
            await AssertStartupFailsAsync<JwtIssuerOptions>(
                factory,
                "JwtSigning:PrivateKeyPath exceeds the maximum supported PEM size");
        });
    }

    public static TheoryData<string> PrivatePublicKeyPaths => new()
    {
        TestJwtTokenFactory.PrivatePkcs1KeyPath,
        TestJwtTokenFactory.PrivateKeyPath,
    };

    [Fact]
    public Task MismatchedPrivateAndPublicKeysFailStartup() =>
        WithTemporaryPrivateKeyAsync(async privateKeyPath =>
        {
            await using var factory = CreateFactory(("JwtSigning:PrivateKeyPath", privateKeyPath));
            await AssertStartupFailsAsync<JwtIssuerOptions>(factory, "matching pair");
        });

    [Fact]
    public Task MalformedPrivateKeyFailsStartup() =>
        WithTemporaryFileAsync("not-an-rsa-private-key", async privateKeyPath =>
        {
            await using var factory = CreateFactory(("JwtSigning:PrivateKeyPath", privateKeyPath));
            await AssertStartupFailsAsync<JwtIssuerOptions>(factory, "valid RSA key pair");
        });

    [Fact]
    public async Task MissingRequiredSigningOptionFailsStartup()
    {
        await using var factory = CreateFactory(("JwtSigning:PrivateKeyPath", string.Empty));

        await AssertStartupFailsAsync<JwtIssuerOptions>(factory, "JwtSigning:PrivateKeyPath");
    }

    [Theory]
    [InlineData("JwtIssuer:Issuer", "unexpected-issuer")]
    [InlineData("JwtIssuer:Audience", "unexpected-audience")]
    public async Task ConflictingLegacyIssuerOrAudienceFailsStartup(string key, string value)
    {
        await using var factory = CreateFactory((key, value));

        await AssertStartupFailsAsync<JwtIssuerOptions>(factory, "conflicts with canonical");
    }

    private static WebApplicationFactory<IdentityApiMarker> CreateFactory(
        params (string Key, string Value)[] overrides) =>
        new WebApplicationFactory<IdentityApiMarker>().WithWebHostBuilder(builder =>
        {
            builder
                .UseEnvironment("Development")
                .UseSetting("ConnectionStrings:IdentityDatabase", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .UseSetting("JwtSigning:PrivateKeyPath", TestJwtTokenFactory.PrivateKeyPath)
                .UseSetting(
                    "DataProtection:KeysPath",
                    Path.Combine(Path.GetTempPath(), $"aicontrolcenter-jwt-startup-dp-{Guid.NewGuid():N}"))
                .UseSetting("IdentityBootstrap:Disabled", "true");

            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }
        });

    private static async Task AssertStartupFailsAsync<TOptions>(
        WebApplicationFactory<IdentityApiMarker> factory,
        string expectedReason)
    {
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health/live");
        });

        var validation = FindOptionsValidationException(exception);
        Assert.True(validation is not null, exception?.ToString());
        Assert.Equal(typeof(TOptions), validation.OptionsType);
        Assert.Contains(validation.Failures, failure =>
            failure.Contains(expectedReason, StringComparison.OrdinalIgnoreCase));
    }

    private static OptionsValidationException? FindOptionsValidationException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        if (exception is OptionsValidationException validation)
        {
            return validation;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(FindOptionsValidationException)
                .FirstOrDefault(candidate => candidate is not null);
        }

        return FindOptionsValidationException(exception.InnerException);
    }

    private static Task WithTemporaryPrivateKeyAsync(Func<string, Task> assertion)
    {
        using var rsa = RSA.Create(2048);
        return WithTemporaryFileAsync(rsa.ExportPkcs8PrivateKeyPem(), assertion);
    }

    private static async Task WithTemporaryFileAsync(string contents, Func<string, Task> assertion)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aicontrolcenter-jwt-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(path, contents);
        try
        {
            await assertion(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiControlCenter.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate AiControlCenter.sln.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityJwtStartupTestGroup
{
    public const string Name = "Identity JWT startup tests";
}
