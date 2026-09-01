namespace AiControlCenter.Identity.Domain;

public sealed class RefreshToken
{
    //Порожній конструктор потрібен EF Core. Коли EF читає запис із PostgreSQL, він повинен створити об’єкт:
    private RefreshToken()
    {
    }

    private RefreshToken(
        Guid id,
        Guid sessionId,
        RefreshTokenHash tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("Refresh token expiration must be after creation.", nameof(expiresAt));
        }

        Id = id;
        SessionId = sessionId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    //Ідентифікатор користувача, якому належить сесія.
    public RefreshTokenHash TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    //Посилання на токен, який замінив поточний.
    public Guid? ReplacedByTokenId { get; private set; }

    public RefreshSession Session { get; private set; } = null!;

    public RefreshToken? ReplacedByToken { get; private set; }

    public static RefreshToken Create(
        Guid sessionId,
        RefreshTokenHash hash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) =>
        new(Guid.NewGuid(), sessionId, hash, createdAt, expiresAt);

    public bool IsCurrent(DateTimeOffset now) =>
        UsedAt is null && ReplacedByTokenId is null && ExpiresAt > now;

    //Метод замінює старий refresh-токен новим.
    public void RotateTo(RefreshToken replacement, DateTimeOffset now)
    {
        if (!IsCurrent(now) || replacement.SessionId != SessionId)
        {
            throw new InvalidOperationException("Refresh token cannot be rotated.");
        }

        UsedAt = now;
        ReplacedByTokenId = replacement.Id;
    }
}
