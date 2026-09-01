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
    public void RefreshTokenRotationUsesOneSessionAndLinksReplacement()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var session = RefreshSession.Create(Guid.NewGuid(), now, now.AddDays(30), "127.0.0.1", "tests");
        var original = RefreshToken.Create(
            session.Id,
            RefreshTokenHash.Create(new string('A', 64)),
            now,
            now.AddDays(30));
        var replacement = RefreshToken.Create(
            session.Id,
            RefreshTokenHash.Create(new string('B', 64)),
            now.AddMinutes(1),
            now.AddDays(30));

        original.RotateTo(replacement, now.AddMinutes(1));

        Assert.False(original.IsCurrent(now.AddMinutes(2)));
        Assert.NotNull(original.UsedAt);
        Assert.Equal(replacement.Id, original.ReplacedByTokenId);
        Assert.True(replacement.IsCurrent(now.AddMinutes(2)));
    }

    [Fact]
    public void RevokedRefreshSessionCannotBeUsed()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var session = RefreshSession.Create(Guid.NewGuid(), now, now.AddDays(30), null, null);

        session.Revoke(now.AddMinutes(1), "logout");

        Assert.False(session.IsActive(now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => session.RecordUse(now.AddMinutes(2)));
    }

    private static User CreateUser(DateTimeOffset now) => User.Create(
        Email.Create("user@example.com"),
        DisplayName.Create("Test User"),
        "password-hash",
        false,
        now);
}
