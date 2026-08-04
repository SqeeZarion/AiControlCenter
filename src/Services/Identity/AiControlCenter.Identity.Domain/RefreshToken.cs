namespace AiControlCenter.Identity.Domain;

public sealed class RefreshToken
{
    private RefreshToken()
    {
    }

    private RefreshToken(
        Guid id,
        Guid userId,
        RefreshTokenHash tokenHash,
        RefreshTokenFamilyId familyId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("Refresh token expiration must be after creation.", nameof(expiresAt));
        }

        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public RefreshTokenHash TokenHash { get; private set; } = null!;

    public RefreshTokenFamilyId FamilyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevocationReason { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public User User { get; private set; } = null!;

    public RefreshToken? ReplacedByToken { get; private set; }

    public static RefreshToken Create(
        Guid userId,
        RefreshTokenHash hash,
        RefreshTokenFamilyId familyId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) =>
        new(Guid.NewGuid(), userId, hash, familyId, createdAt, expiresAt);

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void RotateTo(RefreshToken replacement, DateTimeOffset now)
    {
        if (!IsActive(now) || replacement.FamilyId != FamilyId || replacement.UserId != UserId)
        {
            throw new InvalidOperationException("Refresh token cannot be rotated.");
        }

        RevokedAt = now;
        RevocationReason = "Rotated";
        ReplacedByTokenId = replacement.Id;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevocationReason = string.IsNullOrWhiteSpace(reason) ? "Revoked" : reason.Trim();
    }
}
