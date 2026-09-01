using System.Security.Claims;
using AiControlCenter.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.BuildingBlocks.UnitTests;

public sealed class PasswordChangedPolicyTests
{
    private static readonly string[] ConflictingClaims = ["true", "false"];
    private static readonly string[] DuplicateFalseClaims = ["false", "false"];
    private static readonly string[] DuplicateTrueClaims = ["true", "true"];
    private static readonly string[] ThreeFalseClaims = ["false", "false", "false"];

    [Theory]
    [InlineData("false", true)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    [InlineData("TRUE", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public async Task PasswordChangedRequiresExactFalseClaim(string? claimValue, bool expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformAuthorization();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        if (claimValue is not null)
        {
            claims.Add(new Claim(SecurityClaimNames.PasswordChangeRequired, claimValue));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var result = await authorization.AuthorizeAsync(
            principal,
            resource: null,
            SecurityPolicyNames.PasswordChanged);

        Assert.Equal(expected, result.Succeeded);
    }

    public static TheoryData<string[]> AmbiguousClaimSets => new()
    {
        ConflictingClaims,
        DuplicateFalseClaims,
        DuplicateTrueClaims,
        ThreeFalseClaims,
    };

    [Theory]
    [MemberData(nameof(AmbiguousClaimSets))]
    public async Task PasswordChangedRejectsDuplicateOrConflictingClaims(string[] claimValues)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformAuthorization();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var claims = claimValues
            .Select(value => new Claim(SecurityClaimNames.PasswordChangeRequired, value))
            .Prepend(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var result = await authorization.AuthorizeAsync(
            principal,
            resource: null,
            SecurityPolicyNames.PasswordChanged);

        Assert.False(result.Succeeded);
    }
}
