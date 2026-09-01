using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Infrastructure.Persistence;
using AiControlCenter.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IdentityRsaKeySnapshot AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("IdentityDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:IdentityDatabase is required.");

        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity")));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IRefreshSessionRepository, RefreshSessionRepository>();
        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();
        services.AddSingleton(TimeProvider.System);
        var authentication = configuration.GetSection("Authentication");
        var legacyIssuer = configuration.GetSection("JwtIssuer");
        var signing = configuration.GetSection(JwtSigningOptions.SectionName).Get<JwtSigningOptions>()
            ?? new JwtSigningOptions();
        var issuerOptions = new JwtIssuerOptions
        {
            Issuer = authentication["Issuer"] ?? string.Empty,
            Audience = authentication["Audience"] ?? string.Empty,
            KeyId = authentication["KeyId"] ?? string.Empty,
            Algorithm = authentication["Algorithm"] ?? "RS256",
            PublicKeyPath = authentication["PublicKeyPath"] ?? string.Empty,
            PrivateKeyPath = signing.PrivateKeyPath,
            AccessTokenMinutes = signing.AccessTokenMinutes,
            LegacyIssuer = legacyIssuer["Issuer"],
            LegacyAudience = legacyIssuer["Audience"],
        };
        var validator = new JwtIssuerOptionsValidator(validateKeyMaterial: false);
        var validation = validator.Validate(Options.DefaultName, issuerOptions);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(JwtIssuerOptions),
                validation.Failures);
        }

        if (!IdentityRsaKeySnapshot.TryCreate(issuerOptions, out var keySnapshot, out var keyFailure))
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(JwtIssuerOptions),
                [keyFailure]);
        }

        services.AddSingleton(keySnapshot!);
        services.AddSingleton<IValidateOptions<JwtIssuerOptions>>(validator);
        services.AddOptions<JwtIssuerOptions>()
            .Configure(options =>
            {
                options.Issuer = issuerOptions.Issuer;
                options.Audience = issuerOptions.Audience;
                options.KeyId = issuerOptions.KeyId;
                options.Algorithm = issuerOptions.Algorithm;
                options.PublicKeyPath = issuerOptions.PublicKeyPath;
                options.PrivateKeyPath = issuerOptions.PrivateKeyPath;
                options.AccessTokenMinutes = issuerOptions.AccessTokenMinutes;
                options.LegacyIssuer = issuerOptions.LegacyIssuer;
                options.LegacyAudience = issuerOptions.LegacyAudience;
            })
            .ValidateOnStart();
        services.AddSingleton<IAccessTokenIssuer, RsaAccessTokenIssuer>();

        return keySnapshot!;
    }
}
