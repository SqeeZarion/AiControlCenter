using System.IdentityModel.Tokens.Jwt;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Providers;
using AiControlCenter.Security;

namespace AiControlCenter.Integrations.Api;

public sealed record CreateProviderConnectionRequest(AiProviderKind Provider, string DisplayName, string? ApiKey);
public sealed record ReplaceProviderCredentialRequest(string ApiKey);
public sealed record ConnectGitHubPatRequest(string PersonalAccessToken, string ExpectedGitHubLogin);
public sealed record GitHubOAuthStartRequest(string? RedirectUri = null, string? ReturnUrl = null);
public sealed record ForceLocalCopilotReauthenticationRequest(bool Confirmed);
public sealed record LocalCopilotLogoutRequest(bool Confirmed);

public static class ProviderConnectionEndpoints
{
    public static IEndpointRouteBuilder MapProviderConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/provider-connections")
            .RequireAuthorization(SecurityPolicyNames.AnyPlatformUser, SecurityPolicyNames.PasswordChanged)
            .RequireRateLimiting("provider-credentials");
        group.MapGet("/", async (HttpContext context, ProviderConnectionService service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(UserId(context), token)));
        group.MapGet("/capabilities", (HttpContext context,
            ProviderConnectionFeatureAccess features, LocalCopilotCliAccess localCopilot) =>
            Results.Ok(features.Capabilities(localCopilot.IsAllowed(UserId(context)))));
        group.MapPost("/", async (CreateProviderConnectionRequest request, HttpContext context,
            ProviderConnectionService service, CancellationToken token) =>
            Results.Json(await service.CreateManualAsync(UserId(context), request.Provider, request.DisplayName, request.ApiKey, token),
                statusCode: StatusCodes.Status201Created));
        group.MapPut("/{id:guid}/credential", async (Guid id, ReplaceProviderCredentialRequest request,
            HttpContext context, ProviderConnectionService service, CancellationToken token) =>
            Results.Ok(await service.ReplaceCredentialAsync(id, UserId(context), request.ApiKey, token)));
        group.MapPost("/{id:guid}/validate", async (Guid id, HttpContext context,
            ProviderConnectionService service, CancellationToken token) =>
            Results.Ok(await service.ValidateAsync(id, UserId(context), token)));
        group.MapGet("/{id:guid}/models", async (Guid id, HttpContext context,
            ProviderConnectionService service, CancellationToken token) =>
            Results.Ok(await service.ListModelsAsync(id, UserId(context), token)));
        group.MapGet("/usage", async (int? days, HttpContext context,
            ProviderConnectionService service, CancellationToken token) =>
            Results.Ok(await service.GetUsageAsync(UserId(context), days ?? 30, token)));
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context,
            ProviderConnectionService service, CancellationToken token) =>
        { await service.RevokeAsync(id, UserId(context), token); return Results.NoContent(); });
        group.MapGet("/github/oauth/status", (GitHubOAuthService oauth, ProviderConnectionFeatureAccess features) =>
        {
            features.EnsureOAuthEnabled();
            return Results.Ok(oauth.GetStatus());
        });
        group.MapPut("/github/pat", async (ConnectGitHubPatRequest request, HttpContext context,
            ProviderConnectionService service, ProviderConnectionFeatureAccess features, CancellationToken token) =>
        {
            features.EnsurePatEnabled();
            return Results.Ok(await service.ConnectGitHubPatAsync(UserId(context),
                request.PersonalAccessToken, request.ExpectedGitHubLogin, token));
        });
        group.MapGet("/local-copilot/status", async (HttpContext context,
            LocalCopilotCliAccess access, ProviderConnectionFeatureAccess features, CancellationToken token) =>
        {
            features.EnsureLocalCliEnabled();
            return Results.Ok(await access.StatusAsync(UserId(context), token));
        });
        group.MapPut("/local-copilot", async (HttpContext context,
            LocalCopilotCliAccess access,
            ProviderConnectionFeatureAccess features,
            ProviderConnectionService service,
            CancellationToken token) =>
        {
            var owner = UserId(context);
            features.EnsureLocalCliEnabled();
            access.EnsureAllowed(owner);
            return Results.Ok(await service.ConnectLocalCopilotCliAsync(owner, token));
        });
        group.MapPost("/local-copilot/reconnect", async (HttpContext context,
            ProviderConnectionFeatureAccess features, LocalCopilotCliAccess access,
            LocalCopilotReconnectClient reconnect, CancellationToken token) =>
        {
            var owner = UserId(context);
            features.EnsureLocalCliEnabled();
            access.EnsureAllowed(owner);
            return Results.Accepted(value: await reconnect.StartAsync(
                owner, context.TraceIdentifier, token));
        });
        group.MapPost("/local-copilot/reauthenticate", async (
            ForceLocalCopilotReauthenticationRequest request,
            HttpContext context,
            ProviderConnectionFeatureAccess features,
            LocalCopilotCliAccess access,
            LocalCopilotReconnectClient reconnect,
            CancellationToken token) =>
        {
            var owner = UserId(context);
            features.EnsureLocalCliEnabled();
            access.EnsureAllowed(owner);
            features.EnsureLocalCliReauthenticationEnabled();
            if (!request.Confirmed)
                return Results.BadRequest(new { code = "ReauthenticationConfirmationRequired" });
            return Results.Accepted(value:
                await reconnect.StartForceReauthenticateAsync(
                    confirmed: true, owner, context.TraceIdentifier, token));
        });
        group.MapPost("/local-copilot/logout", async (LocalCopilotLogoutRequest request,
            HttpContext context, ProviderConnectionFeatureAccess features,
            LocalCopilotCliAccess access, LocalCopilotReconnectClient reconnect,
            CancellationToken token) =>
        {
            var owner = UserId(context);
            features.EnsureLocalCliEnabled();
            access.EnsureAllowed(owner);
            features.EnsureLocalCliLogoutEnabled();
            if (!request.Confirmed)
                return Results.BadRequest(new { code = "LocalLogoutConfirmationRequired" });
            return Results.Ok(await reconnect.LogoutAsync(confirmed: true, token));
        });
        group.MapGet("/local-copilot/reconnect/{operationId:guid}", async (Guid operationId,
            HttpContext context, ProviderConnectionFeatureAccess features, LocalCopilotCliAccess access,
            LocalCopilotReconnectClient reconnect, LocalCopilotReconnectCoordinator coordinator,
            CancellationToken token) =>
        {
            var owner = UserId(context);
            features.EnsureLocalCliEnabled();
            access.EnsureAllowed(owner);
            var result = await reconnect.GetAsync(operationId, token);
            if (result.State == LocalCopilotReconnectState.Validating)
                result = await coordinator.CompleteAsync(owner, result, token);
            return Results.Ok(result);
        }).DisableRateLimiting();
        group.MapGet("/local-copilot/reconnect/current", async (HttpContext context,
            ProviderConnectionFeatureAccess features, LocalCopilotCliAccess access,
            LocalCopilotReconnectClient reconnect,
            LocalCopilotReconnectCoordinator coordinator,
            CancellationToken token) =>
        {
            var owner = UserId(context);
            features.EnsureLocalCliEnabled();
            access.EnsureAllowed(owner);
            var current = await reconnect.GetCurrentAsync(token);
            if (current?.State == LocalCopilotReconnectState.Validating)
                current = await coordinator.CompleteAsync(owner, current, token);
            return current is null ? Results.NoContent() : Results.Ok(current);
        }).DisableRateLimiting();
        group.MapPost("/local-copilot/reconnect/{operationId:guid}/cancel", async (Guid operationId,
            HttpContext context, ProviderConnectionFeatureAccess features, LocalCopilotCliAccess access,
            LocalCopilotReconnectClient reconnect, CancellationToken token) =>
        {
            var owner = UserId(context);
            features.EnsureLocalCliEnabled();
            access.EnsureAllowed(owner);
            return Results.Ok(await reconnect.CancelAsync(operationId, token));
        });
        group.MapPost("/github/oauth/start", async (GitHubOAuthStartRequest request, HttpContext context,
            GitHubOAuthService oauth, ProviderConnectionFeatureAccess features, CancellationToken token) =>
        {
            features.EnsureOAuthEnabled();
            var result = await oauth.StartAsync(
                UserId(context), request.RedirectUri, request.ReturnUrl, token);
            return Results.Json(result, statusCode: result.Status == GitHubOAuthConfigurationStatus.NotConfigured
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status200OK);
        });
        endpoints.MapGet("/v1/provider-connections/github/oauth/callback", async (string state, string code,
            GitHubOAuthService oauth, ProviderConnectionFeatureAccess features, CancellationToken token) =>
        { features.EnsureOAuthEnabled(); await oauth.CompleteAsync(state, code, token); return Results.Redirect("/providers?connected=true"); })
            .RequireRateLimiting("provider-credentials")
            .AllowAnonymous();
        return endpoints;
    }
    private static Guid UserId(HttpContext context)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(subject, out var id) && id != Guid.Empty ? id
            : throw new InvalidOperationException("Authenticated token does not contain a valid subject.");
    }
}
