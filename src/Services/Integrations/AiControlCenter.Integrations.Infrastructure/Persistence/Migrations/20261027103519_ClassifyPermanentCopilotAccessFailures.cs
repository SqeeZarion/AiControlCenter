using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class ClassifyPermanentCopilotAccessFailures : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.AlterColumn<string>(
            name: "status",
            schema: "integrations",
            table: "provider_connections",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(16)",
            oldMaxLength: 16);

        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections",
            sql: "status IN ('Active','Degraded','RequiresReconnect','NotEntitled','PolicyBlocked','OAuthAppUnsupported','InsufficientPermissions','QuotaExhausted','Invalid','Revoked')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.Sql("""
            UPDATE integrations.provider_connections
            SET status = 'Degraded'
            WHERE status IN ('PolicyBlocked','OAuthAppUnsupported','InsufficientPermissions');
            """);

        migrationBuilder.AlterColumn<string>(
            name: "status",
            schema: "integrations",
            table: "provider_connections",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(32)",
            oldMaxLength: 32);

        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections",
            sql: "status IN ('Active','Degraded','RequiresReconnect','NotEntitled','QuotaExhausted','Invalid','Revoked')");
    }
}
