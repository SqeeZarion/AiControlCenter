namespace AiControlCenter.Identity.Domain;
//Коли refresh-токен оновлюється, створюється новий токен, але RefreshSession залишається тією самою.
//створений для того щоб при оновлені токена не треба було шукати ланцюг сессій


public sealed class RefreshSession
{
    private RefreshSession()
    {
    }

    private RefreshSession(
        Guid id,
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string? createdByIp,
        string? userAgent)
    {
        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("Refresh session expiration must be after creation.", nameof(expiresAt));
        }

        Id = id;
        UserId = userId;
        CreatedAt = createdAt;
        LastUsedAt = createdAt;
        ExpiresAt = expiresAt;
        CreatedByIp = NormalizeMetadata(createdByIp, 64);
        UserAgent = NormalizeMetadata(userAgent, 512);
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastUsedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? CreatedByIp { get; private set; }

    public string? UserAgent { get; private set; }

    public uint Version { get; private set; }

    public User User { get; private set; } = null!;

    public ICollection<RefreshToken> Tokens { get; private set; } = [];

    public static RefreshSession Create(
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string? createdByIp,
        string? userAgent) =>
        new(Guid.NewGuid(), userId, createdAt, expiresAt, createdByIp, userAgent);

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void RecordUse(DateTimeOffset now)
    {
        if (!IsActive(now))
        {
            throw new InvalidOperationException("A revoked or expired refresh session cannot be used.");
        }

        LastUsedAt = now;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = string.IsNullOrWhiteSpace(reason) ? "Revoked" : reason.Trim();
    }

    private static string? NormalizeMetadata(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
