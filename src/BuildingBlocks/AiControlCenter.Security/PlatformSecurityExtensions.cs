using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Security;

public static class PlatformSecurityExtensions
{
    //Метод реєструє повну JWT authentication для поточного сервісу.
    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration) =>
        AddPlatformAuthenticationCore(services, configuration, preloadedPublicKey: null);

    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        RSA preloadedPublicKey) =>
        AddPlatformAuthenticationCore(services, configuration, preloadedPublicKey);

    private static IServiceCollection AddPlatformAuthenticationCore(
        IServiceCollection services,
        IConfiguration configuration,
        RSA? preloadedPublicKey)
    {
        //Якщо секції немає, програма відразу не запуститься. Це fail-fast: краще одразу побачити неправильний конфіг, ніж запустити API без нормальної перевірки токенів.
        var settings = configuration.GetSection(PlatformAuthenticationOptions.SectionName)
            .Get<PlatformAuthenticationOptions>()
            ?? throw new InvalidOperationException("Authentication configuration is required.");

        var validator = preloadedPublicKey is null
            ? new PlatformAuthenticationOptionsValidator()
            : new PlatformAuthenticationOptionsValidator(preloadedPublicKey);
        var validation = validator.Validate(Options.DefaultName, settings);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(PlatformAuthenticationOptions),
                validation.Failures);
        }

        services.AddSingleton<IValidateOptions<PlatformAuthenticationOptions>>(validator);
        services.AddOptions<PlatformAuthenticationOptions>()
            .Bind(configuration.GetSection(PlatformAuthenticationOptions.SectionName))
            .ValidateOnStart();

        // Процес:
        //
        // Створюється RSA-об’єкт.
        //     З файлу читається публічний PEM-ключ.
        //     Ключ перетворюється на RsaSecurityKey.
        //     Йому встановлюється KeyId.
        //
        //     Цей ключ потім використовується для перевірки цифрового підпису JWT.
        RSA loadedRsa;
        if (preloadedPublicKey is not null)
        {
            loadedRsa = preloadedPublicKey;
        }
        else if (!RsaPublicKeyLoader.TryLoad(settings.PublicKeyPath, out var rsa, out var keyFailure))
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(PlatformAuthenticationOptions),
                [keyFailure]);
        }
        else
        {
            loadedRsa = rsa!;
            services.AddSingleton(loadedRsa);
        }
        var signingKey = new RsaSecurityKey(loadedRsa)
        {
            KeyId = settings.KeyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };

        //Bearer означає: хто володіє токеном, той може його використати, тому токен не можна записувати в логи або передавати стороннім особам.
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    //Сервіс перевіряє, чи JWT підписаний приватним ключем Identity.
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    TryAllIssuerSigningKeys = false,
                    IssuerSigningKeyResolver = (_, _, keyId, _) =>
                        string.Equals(keyId, settings.KeyId, StringComparison.Ordinal)
                            ? [signingKey]
                            : [],
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    //Непідписаний JWT не буде прийнятий.
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = SecurityClaimNames.Role,
                    ValidAlgorithms = [settings.Algorithm],
                };
                options.Events = new JwtBearerEvents
                {
                    // Цей код:
                    //
                    // Перевіряє, що запит іде саме до /hubs/system.
                    // Шукає access_token.
                    // Передає його JWT middleware.
                    OnMessageReceived = context =>
                    {
                        if (context.HttpContext.Request.Path.StartsWithSegments("/hubs/system")
                            && context.Request.Query.TryGetValue("access_token", out var token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                    //Перевірка призначення токена
                    OnTokenValidated = context =>
                    {
                        if (!context.Principal!.HasClaim(
                                SecurityClaimNames.TokenUse,
                                SecurityClaimNames.AccessTokenUse))
                        {
                            context.Fail("The token is not an access token.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }

    //перевіряє роль користувача
    public static IServiceCollection AddPlatformAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            //fallback policy все одно вимагатиме авторизованого користувача.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
            options.AddPolicy(SecurityPolicyNames.AdminOnly, policy => policy.RequireRole("Admin"));
            options.AddPolicy(SecurityPolicyNames.AdminOrDeveloper, policy =>
                policy.RequireRole("Admin", "Developer"));
            options.AddPolicy(SecurityPolicyNames.AnyPlatformUser, policy =>
                policy.RequireRole("Admin", "Developer", "User"));
            //Зміна тимчасового пароля
            options.AddPolicy(SecurityPolicyNames.PasswordChanged, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var claims = context.User.FindAll(SecurityClaimNames.PasswordChangeRequired).ToArray();
                    return claims.Length == 1
                        && string.Equals(claims[0].Value, "false", StringComparison.Ordinal);
                }));
        });

        return services;
    }

    public static TBuilder RequirePlatformAuthorization<TBuilder>(
        this TBuilder builder,
        string policy)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization(policy);
        return builder;
    }

}
