using System.Security.Claims;
using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using AiControlCenter.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace AiControlCenter.Identity.Api;

public static class IdentityEndpointExtensions
{
    private const string RefreshCookieName = "aicontrolcenter.refresh";

    public static IEndpointRouteBuilder MapIdentityEndpoints(
        this IEndpointRouteBuilder endpoints,
        IHostEnvironment environment)
    {
        var auth = endpoints.MapGroup("/v1/auth");
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            {
                HttpOnly = false,
                Secure = !environment.IsDevelopment(),
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
            });
            return Results.Ok();
        }).AllowAnonymous();

        auth.MapPost("/login", async (
            LoginRequest request,
            IdentityApplicationService service,
            TimeProvider timeProvider,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var session = await service.LoginAsync(request, cancellationToken);
            SetRefreshCookie(context, session, environment);
            return Results.Ok(ToResponse(session, timeProvider.GetUtcNow()));
        })
        .AddEndpointFilter<ValidationFilter<LoginRequest>>()
        .RequireRateLimiting("identity-auth")
        .AllowAnonymous();

        auth.MapPost("/refresh", async (
            IdentityApplicationService service,
            TimeProvider timeProvider,
            IAntiforgery antiforgery,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            context.Request.Cookies.TryGetValue(RefreshCookieName, out var rawToken);
            var session = await service.RefreshSessionAsync(rawToken ?? string.Empty, cancellationToken);
            SetRefreshCookie(context, session, environment);
            return Results.Ok(ToResponse(session, timeProvider.GetUtcNow()));
        })
        .AddEndpointFilter<OriginValidationFilter>()
        .RequireRateLimiting("identity-auth")
        .AllowAnonymous();

        auth.MapPost("/logout", async (
            IdentityApplicationService service,
            IAntiforgery antiforgery,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            context.Request.Cookies.TryGetValue(RefreshCookieName, out var rawToken);
            await service.LogoutCurrentSessionAsync(rawToken, cancellationToken);
            DeleteRefreshCookie(context, environment);
            return Results.NoContent();
        })
        .AddEndpointFilter<OriginValidationFilter>()
        .AllowAnonymous();

        auth.MapPost("/logout-all", async (
            ClaimsPrincipal principal,
            IdentityApplicationService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            await service.LogoutAllSessionsAsync(GetUserId(principal), cancellationToken);
            DeleteRefreshCookie(context, environment);
            return Results.NoContent();
        }).RequireAuthorization();

        var me = endpoints.MapGroup("/v1/users/me").RequireAuthorization();
        me.MapGet("/", async (
            ClaimsPrincipal principal,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetCurrentUserAsync(GetUserId(principal), cancellationToken)));
        me.MapPost("/change-password", async (
            ChangePasswordRequest request,
            ClaimsPrincipal principal,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
        {
            await service.ChangeOwnPasswordAsync(GetUserId(principal), request, cancellationToken);
            return Results.NoContent();
        }).AddEndpointFilter<ValidationFilter<ChangePasswordRequest>>();

        var adminUsers = endpoints.MapGroup("/v1/users")
            .RequireAuthorization(SecurityPolicyNames.AdminOnly, SecurityPolicyNames.PasswordChanged);
        adminUsers.MapGet("/", async (
            int? page,
            int? pageSize,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListUsersAsync(page ?? 1, pageSize ?? 25, cancellationToken)));
        adminUsers.MapPost("/", async (
            CreateUserRequest request,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var user = await service.CreateUserAsync(request, cancellationToken);
            return Results.Created($"/v1/users/{user.Id}", user);
        }).AddEndpointFilter<ValidationFilter<CreateUserRequest>>();
        adminUsers.MapGet("/{id:guid}", async (
            Guid id,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetUserAsync(id, cancellationToken)));
        adminUsers.MapPatch("/{id:guid}/status", async (
            Guid id,
            ChangeUserStatusRequest request,
            IdentityApplicationService service,
            CancellationToken cancellationToken) => request.Status switch
        {
            UserStatus.Active => Results.Ok(await service.ActivateUserAsync(id, cancellationToken)),
            UserStatus.Blocked => Results.Ok(await service.BlockUserAsync(id, cancellationToken)),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Status)] = ["Unknown user status."],
            }),
        });
        adminUsers.MapPut("/{id:guid}/roles", async (
            Guid id,
            ReplaceUserRolesRequest request,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ReplaceUserRolesAsync(id, request, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<ReplaceUserRolesRequest>>();
        adminUsers.MapPost("/{id:guid}/reset-password", async (
            Guid id,
            ResetUserPasswordRequest request,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
        {
            await service.ResetUserPasswordAsync(id, request, cancellationToken);
            return Results.NoContent();
        }).AddEndpointFilter<ValidationFilter<ResetUserPasswordRequest>>();
        adminUsers.MapPost("/{id:guid}/sessions/revoke", async (
            Guid id,
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
        {
            await service.RevokeUserSessionsAsync(id, cancellationToken);
            return Results.NoContent();
        });

        endpoints.MapGet("/v1/roles", async (
            IdentityApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListRolesAsync(cancellationToken)))
            .RequireAuthorization(SecurityPolicyNames.AdminOnly, SecurityPolicyNames.PasswordChanged);

        return endpoints;
    }

    private static object ToResponse(AuthSessionResult session, DateTimeOffset now) => new
    {
        accessToken = session.AccessToken.Token,
        tokenType = "Bearer",
        expiresInSeconds = Math.Max(0, (int)(session.AccessToken.ExpiresAt - now).TotalSeconds),
        user = session.User,
    };

    private static Guid GetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var userId)
            ? userId
            : throw new IdentityAuthenticationException();

    private static void SetRefreshCookie(
        HttpContext context,
        AuthSessionResult session,
        IHostEnvironment environment) =>
        context.Response.Cookies.Append(RefreshCookieName, session.RefreshToken, CookieOptions(environment, session.RefreshTokenExpiresAt));

    private static void DeleteRefreshCookie(HttpContext context, IHostEnvironment environment) =>
        context.Response.Cookies.Delete(RefreshCookieName, CookieOptions(environment, DateTimeOffset.UnixEpoch));

    private static CookieOptions CookieOptions(IHostEnvironment environment, DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = "/api/identity/v1/auth",
        Expires = expires,
        IsEssential = true,
    };
}
