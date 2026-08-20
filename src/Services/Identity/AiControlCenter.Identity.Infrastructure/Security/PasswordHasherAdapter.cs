using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using AppPasswordVerificationResult = AiControlCenter.Identity.Application.PasswordVerificationResult;

namespace AiControlCenter.Identity.Infrastructure.Security;

//Хешує і перевіряє паролі користувачів
internal sealed class PasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<User> hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 210_000,
    }));
    private readonly string dummyHash;

    public PasswordHasherAdapter() => dummyHash = hasher.HashPassword(null!, "timing-only-password");

    public string Hash(string password) => hasher.HashPassword(null!, password);

    public AppPasswordVerificationResult Verify(string passwordHash, string providedPassword) =>
        hasher.VerifyHashedPassword(null!, passwordHash, providedPassword) switch
        {
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success =>
                AppPasswordVerificationResult.Success,
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.SuccessRehashNeeded =>
                AppPasswordVerificationResult.SuccessRehashNeeded,
            _ => AppPasswordVerificationResult.Failed,
        };

    public void VerifyUnknown(string providedPassword) =>
        _ = hasher.VerifyHashedPassword(null!, dummyHash, providedPassword);
}
