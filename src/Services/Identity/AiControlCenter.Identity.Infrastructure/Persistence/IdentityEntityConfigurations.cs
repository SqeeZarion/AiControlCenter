using AiControlCenter.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiControlCenter.Identity.Infrastructure.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(user => user.Email)
            .HasColumnName("normalized_email")
            .HasMaxLength(320)
            .HasConversion(email => email.Value, value => Email.Create(value));
        builder.HasIndex(user => user.Email).IsUnique().HasDatabaseName("ux_users_normalized_email");
        builder.Property(user => user.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(100)
            .HasConversion(name => name.Value, value => DisplayName.Create(value));
        builder.Property(user => user.PasswordHash).HasColumnName("password_hash").HasMaxLength(1024);
        builder.Property(user => user.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(user => user.MustChangePassword).HasColumnName("must_change_password");
        builder.Property(user => user.AccessFailedCount).HasColumnName("access_failed_count");
        builder.Property(user => user.LockoutEnd).HasColumnName("lockout_end");
        builder.Property(user => user.CreatedAt).HasColumnName("created_at");
        builder.Property(user => user.UpdatedAt).HasColumnName("updated_at");
        builder.Property(user => user.LastLoginAt).HasColumnName("last_login_at");
        builder.Property(user => user.Version).HasColumnName("xmin").IsRowVersion();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(role => role.Name)
            .HasColumnName("name")
            .HasMaxLength(32)
            .HasConversion(name => name.Value, value => RoleName.Create(value));
        builder.HasIndex(role => role.Name).IsUnique().HasDatabaseName("ux_roles_name");
        builder.Property(role => role.Description).HasColumnName("description").HasMaxLength(256);
        builder.HasData(Role.SystemRoles);
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(userRole => new { userRole.UserId, userRole.RoleId });
        builder.Property(userRole => userRole.UserId).HasColumnName("user_id");
        builder.Property(userRole => userRole.RoleId).HasColumnName("role_id");
        builder.HasIndex(userRole => userRole.RoleId).HasDatabaseName("ix_user_roles_role_id");
        builder.HasOne(userRole => userRole.User)
            .WithMany(user => user.UserRoles)
            .HasForeignKey(userRole => userRole.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(userRole => userRole.Role)
            .WithMany(role => role.UserRoles)
            .HasForeignKey(userRole => userRole.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(token => token.UserId).HasColumnName("user_id");
        builder.Property(token => token.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(64)
            .HasConversion(hash => hash.Value, value => RefreshTokenHash.Create(value));
        builder.HasIndex(token => token.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_hash");
        builder.Property(token => token.FamilyId)
            .HasColumnName("family_id")
            .HasConversion(id => id.Value, value => new RefreshTokenFamilyId(value));
        builder.HasIndex(token => token.FamilyId).HasDatabaseName("ix_refresh_tokens_family_id");
        builder.HasIndex(token => new { token.UserId, token.ExpiresAt })
            .HasDatabaseName("ix_refresh_tokens_user_expiration");
        builder.Property(token => token.CreatedAt).HasColumnName("created_at");
        builder.Property(token => token.ExpiresAt).HasColumnName("expires_at");
        builder.Property(token => token.RevokedAt).HasColumnName("revoked_at");
        builder.Property(token => token.RevocationReason).HasColumnName("revocation_reason").HasMaxLength(128);
        builder.Property(token => token.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
        builder.HasOne(token => token.User)
            .WithMany(user => user.RefreshTokens)
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(token => token.ReplacedByToken)
            .WithMany()
            .HasForeignKey(token => token.ReplacedByTokenId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(token => token.ReplacedByTokenId)
            .HasDatabaseName("ix_refresh_tokens_replaced_by_token_id");
    }
}
