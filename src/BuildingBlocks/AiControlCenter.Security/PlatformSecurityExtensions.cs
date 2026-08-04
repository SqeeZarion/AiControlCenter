using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace AiControlCenter.Security;

public static class PlatformSecurityExtensions
{
    //Метод реєструє повну JWT authentication для поточного сервісу.
    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        //Якщо секції немає, програма відразу не запуститься. Це fail-fast: краще одразу побачити неправильний конфіг, ніж запустити API без нормальної перевірки токенів.
        var settings = configuration.GetSection(PlatformAuthenticationOptions.SectionName)
            .Get<PlatformAuthenticationOptions>()
            ?? throw new InvalidOperationException("Authentication configuration is required.");

        Validate(settings);

        // Процес:
        //
        // Створюється RSA-об’єкт.
        //     З файлу читається публічний PEM-ключ.
        //     Ключ перетворюється на RsaSecurityKey.
        //     Йому встановлюється KeyId.
        //
        //     Цей ключ потім використовується для перевірки цифрового підпису JWT.
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(settings.PublicKeyPath));
        var signingKey = new RsaSecurityKey(rsa) { KeyId = settings.KeyId };

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = SecurityClaimNames.Role,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.HttpContext.Request.Path.StartsWithSegments("/hubs/system")
                            && context.Request.Query.TryGetValue("access_token", out var token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
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

    public static IServiceCollection AddPlatformAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
            options.AddPolicy(SecurityPolicyNames.AdminOnly, policy => policy.RequireRole("Admin"));
            options.AddPolicy(SecurityPolicyNames.AdminOrDeveloper, policy =>
                policy.RequireRole("Admin", "Developer"));
            options.AddPolicy(SecurityPolicyNames.AnyPlatformUser, policy =>
                policy.RequireRole("Admin", "Developer", "User"));
            options.AddPolicy(SecurityPolicyNames.PasswordChanged, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    !context.User.HasClaim(SecurityClaimNames.PasswordChangeRequired, "true")));
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

    private static void Validate(PlatformAuthenticationOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Issuer)
            || string.IsNullOrWhiteSpace(settings.Audience)
            || string.IsNullOrWhiteSpace(settings.KeyId)
            || string.IsNullOrWhiteSpace(settings.PublicKeyPath))
        {
            throw new InvalidOperationException(
                "Authentication issuer, audience, key id and public key path are required.");
        }

        if (settings.ClockSkewSeconds is < 0 or > 30)
        {
            throw new InvalidOperationException("Authentication clock skew must be between 0 and 30 seconds.");
        }

        if (!File.Exists(settings.PublicKeyPath))
        {
            throw new FileNotFoundException("The JWT public key file was not found.", settings.PublicKeyPath);
        }
    }
}
