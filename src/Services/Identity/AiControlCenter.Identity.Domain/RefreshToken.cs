namespace AiControlCenter.Identity.Domain;

public sealed class RefreshToken
{
    //Порожній конструктор потрібен EF Core. Коли EF читає запис із PostgreSQL, він повинен створити об’єкт:
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

    //Ідентифікатор користувача, якому належить сесія.
    public RefreshTokenHash TokenHash { get; private set; } = null!;

    //Ідентифікатор сім’ї токенів. Усі токени, створені під час послідовних refresh, належать до однієї family
    public RefreshTokenFamilyId FamilyId { get; private set; }

    //Час створення та завершення дії.
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    //Час відкликання токена.
    public DateTimeOffset? RevokedAt { get; private set; }

    //Причина відкликання:
    public string? RevocationReason { get; private set; }

    //Посилання на токен, який замінив поточний.
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

    //Перевірка активності Він не відкликаний. Його строк дії ще не завершився.
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    //Метод замінює старий refresh-токен новим.
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

    //Звичайне відкликання, викликається коли заблокований користувач
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
