using AiControlCenter.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiControlCenter.Identity.Infrastructure.Persistence;

//створює звязки між конфігураціями й бд у PostgreSQL.

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
        builder.Property(token => token.SessionId).HasColumnName("session_id");
        builder.Property(token => token.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(64)
            .HasConversion(hash => hash.Value, value => RefreshTokenHash.Create(value));
        builder.HasIndex(token => token.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_hash");
        builder.Property(token => token.CreatedAt).HasColumnName("created_at");
        builder.Property(token => token.ExpiresAt).HasColumnName("expires_at");
        builder.Property(token => token.UsedAt).HasColumnName("used_at");
        builder.Property(token => token.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
        builder.HasOne(token => token.Session)
            .WithMany(session => session.Tokens)
            .HasForeignKey(token => token.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(token => token.ReplacedByToken)
            .WithMany()
            .HasForeignKey(token => token.ReplacedByTokenId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(token => token.ReplacedByTokenId)
            .HasDatabaseName("ix_refresh_tokens_replaced_by_token_id");
        builder.HasIndex(token => token.SessionId).HasDatabaseName("ix_refresh_tokens_session_id");
    }
}

internal sealed class RefreshSessionConfiguration : IEntityTypeConfiguration<RefreshSession>
{
    public void Configure(EntityTypeBuilder<RefreshSession> builder)
    {
        builder.ToTable("refresh_sessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(session => session.UserId).HasColumnName("user_id");
        builder.Property(session => session.CreatedAt).HasColumnName("created_at");
        builder.Property(session => session.LastUsedAt).HasColumnName("last_used_at");
        builder.Property(session => session.ExpiresAt).HasColumnName("expires_at");
        builder.Property(session => session.RevokedAt).HasColumnName("revoked_at");
        builder.Property(session => session.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(128);
        builder.Property(session => session.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
        builder.Property(session => session.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(session => session.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasIndex(session => new { session.UserId, session.ExpiresAt })
            .HasDatabaseName("ix_refresh_sessions_user_expiration");
        builder.HasOne(session => session.User)
            .WithMany(user => user.RefreshSessions)
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
