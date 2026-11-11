using AiControlCenter.Integrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiControlCenter.Integrations.Infrastructure.Persistence;

internal sealed class ProviderConnectionConfiguration : IEntityTypeConfiguration<ProviderConnection>
{
    public void Configure(EntityTypeBuilder<ProviderConnection> builder)
    {
        builder.ToTable("provider_connections", table =>
        {
            table.HasCheckConstraint("ck_provider_connections_provider", "provider IN ('GitHubCopilot','GitHubCopilotLocalCli','OpenAi','XAi','DeterministicTest')");
            table.HasCheckConstraint("ck_provider_connections_status",
                "status IN ('Active','Degraded','RequiresReconnect','NotEntitled','PolicyBlocked','OAuthAppUnsupported','InsufficientPermissions','QuotaExhausted','Invalid','Revoked')");
            table.HasCheckConstraint("ck_provider_connections_timestamps", "updated_at >= created_at AND (revoked_at IS NULL OR revoked_at >= created_at)");
            table.HasCheckConstraint("ck_provider_connections_secret", "(provider IN ('DeterministicTest','GitHubCopilotLocalCli')) OR (status = 'Revoked') OR protected_credential IS NOT NULL");
            table.HasCheckConstraint("ck_provider_connections_credential_version", "credential_version > 0");
            table.HasCheckConstraint("ck_provider_connections_quota_source", "quota_source IN ('ProviderReported','Estimated','Unavailable')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(x => x.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(ProviderConnection.DisplayNameMaxLength);
        builder.Property(x => x.ProtectedCredential).HasColumnName("protected_credential").HasMaxLength(ProviderConnection.SecretMaxLength);
        builder.Property(x => x.CredentialFingerprint).HasColumnName("credential_fingerprint").HasMaxLength(32);
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CredentialVersion).HasColumnName("credential_version").HasDefaultValue(1);
        builder.Property(x => x.AvailableModelsJson).HasColumnName("available_models_json").HasColumnType("jsonb");
        builder.Property(x => x.LastSyncedAt).HasColumnName("last_synced_at");
        builder.Property(x => x.QuotaSource).HasColumnName("quota_source").HasConversion<string>().HasMaxLength(24)
            .HasDefaultValue(ProviderMetricSource.Unavailable);
        builder.Property(x => x.QuotaState).HasColumnName("quota_state").HasMaxLength(64).HasDefaultValue("Unavailable");
        builder.Property(x => x.LastErrorCategory).HasColumnName("last_error_category").HasMaxLength(64);
        builder.Property(x => x.LastSafeError).HasColumnName("last_safe_error").HasMaxLength(256);
        builder.Property(x => x.ExternalAccountId).HasColumnName("external_account_id")
            .HasMaxLength(ProviderConnection.ExternalAccountIdMaxLength);
        builder.Property(x => x.ExternalAccountLogin).HasColumnName("external_account_login")
            .HasMaxLength(ProviderConnection.ExternalAccountLoginMaxLength);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.LastValidatedAt).HasColumnName("last_validated_at");
        builder.Property(x => x.LastSuccessfulValidationAt).HasColumnName("last_successful_validation_at");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        builder.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasIndex(x => new { x.OwnerUserId, x.Provider, x.DisplayName }).IsUnique()
            .HasDatabaseName("ux_provider_connections_owner_provider_name");
    }
}

internal sealed class ProviderOAuthStateConfiguration : IEntityTypeConfiguration<ProviderOAuthState>
{
    public void Configure(EntityTypeBuilder<ProviderOAuthState> builder)
    {
        builder.ToTable("provider_oauth_states");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(x => x.StateHash).HasColumnName("state_hash").HasMaxLength(64);
        builder.Property(x => x.CodeVerifierProtected).HasColumnName("code_verifier_protected").HasMaxLength(4096);
        builder.Property(x => x.RedirectUri).HasColumnName("redirect_uri").HasMaxLength(512);
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        builder.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasIndex(x => x.StateHash).IsUnique().HasDatabaseName("ux_provider_oauth_states_hash");
    }
}

internal sealed class ProviderExecutionReceiptConfiguration : IEntityTypeConfiguration<ProviderExecutionReceipt>
{
    public void Configure(EntityTypeBuilder<ProviderExecutionReceipt> builder)
    {
        builder.ToTable("provider_execution_receipts", table =>
        {
            table.HasCheckConstraint("ck_provider_execution_receipts_status", "status IN ('Pending','Completed','Released','Ambiguous')");
            table.HasCheckConstraint("ck_provider_execution_receipts_lifecycle",
                "(status = 'Pending' AND result_json IS NULL AND completed_at IS NULL) OR "
                + "(status IN ('Completed','Released','Ambiguous') AND result_json IS NOT NULL AND completed_at >= created_at)");
            table.HasCheckConstraint("ck_provider_execution_receipts_costs",
                "reserved_cost_usd >= 0 AND (actual_cost_usd IS NULL OR actual_cost_usd >= 0)");
            table.HasCheckConstraint("ck_provider_execution_receipts_requests",
                "reserved_request_count > 0 AND actual_request_count >= 0 "
                + "AND actual_request_count <= reserved_request_count");
        });
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(receipt => receipt.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(receipt => receipt.ConnectionId).HasColumnName("connection_id");
        builder.Property(receipt => receipt.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(32);
        builder.Property(receipt => receipt.IdempotencyKey).HasColumnName("idempotency_key")
            .HasMaxLength(ProviderExecutionReceipt.IdempotencyKeyMaxLength);
        builder.Property(receipt => receipt.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(receipt => receipt.ResultJson).HasColumnName("result_json")
            .HasMaxLength(ProviderExecutionReceipt.ResultJsonMaxLength);
        builder.Property(receipt => receipt.ReservedCostUsd).HasColumnName("reserved_cost_usd").HasPrecision(18, 8);
        builder.Property(receipt => receipt.ActualCostUsd).HasColumnName("actual_cost_usd").HasPrecision(18, 8);
        builder.Property(receipt => receipt.ReservedRequestCount).HasColumnName("reserved_request_count");
        builder.Property(receipt => receipt.ActualRequestCount).HasColumnName("actual_request_count");
        builder.Property(receipt => receipt.CreatedAt).HasColumnName("created_at");
        builder.Property(receipt => receipt.CompletedAt).HasColumnName("completed_at");
        builder.Property(receipt => receipt.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasIndex(receipt => new { receipt.OwnerUserId, receipt.IdempotencyKey }).IsUnique()
            .HasDatabaseName("ux_provider_execution_receipts_owner_idempotency");
        builder.HasIndex(receipt => new { receipt.ConnectionId, receipt.CreatedAt })
            .HasDatabaseName("ix_provider_execution_receipts_connection_created");
    }
}
