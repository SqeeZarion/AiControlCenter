using AiControlCenter.Identity.Domain;
using Microsoft.Extensions.Time.Testing;

namespace AiControlCenter.Identity.UnitTests;

public sealed class IdentityDomainTests
{
    [Fact]
    public void EmailIsTrimmedAndNormalized()
    {
        var email = Email.Create("  Admin@Example.COM ");
        Assert.Equal("admin@example.com", email.Value);
    }

    [Fact]
    public void OnlySystemRoleNamesAreAccepted()
    {
        Assert.Equal(RoleName.Admin, RoleName.Create("Admin"));
        Assert.Throws<ArgumentException>(() => RoleName.Create("SuperAdmin"));
    }

    [Fact]
    public void BlockedUserCannotLogin()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var user = CreateUser(time.GetUtcNow());

        user.Block(time.GetUtcNow());

        Assert.False(user.CanLogin(time.GetUtcNow()));
    }

    [Fact]
    public void FiveFailedLoginsCreateTemporaryLockout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var user = CreateUser(time.GetUtcNow());

        for (var attempt = 0; attempt < 5; attempt++)
        {
            user.RecordFailedLogin(time.GetUtcNow(), 5, TimeSpan.FromMinutes(15));
        }

        Assert.False(user.CanLogin(time.GetUtcNow()));
        time.Advance(TimeSpan.FromMinutes(16));
        Assert.True(user.CanLogin(time.GetUtcNow()));
    }

    [Fact]
    public void RefreshTokenRotationRevokesOriginalAndLinksReplacement()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var family = RefreshTokenFamilyId.New();
        var original = RefreshToken.Create(
            Guid.NewGuid(),
            RefreshTokenHash.Create(new string('A', 64)),
            family,
            now,
            now.AddDays(30));
        var replacement = RefreshToken.Create(
            original.UserId,
            RefreshTokenHash.Create(new string('B', 64)),
            family,
            now.AddMinutes(1),
            now.AddDays(30));

        original.RotateTo(replacement, now.AddMinutes(1));

        Assert.False(original.IsActive(now.AddMinutes(2)));
        Assert.Equal(replacement.Id, original.ReplacedByTokenId);
        Assert.True(replacement.IsActive(now.AddMinutes(2)));
    }

    private static User CreateUser(DateTimeOffset now) => User.Create(
        Email.Create("user@example.com"),
        DisplayName.Create("Test User"),
        "password-hash",
        false,
        now);
}
