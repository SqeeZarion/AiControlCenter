using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProviderExternalIdentityAndValidationStates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.AddColumn<string>(
            name: "external_account_id",
            schema: "integrations",
            table: "provider_connections",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "external_account_login",
            schema: "integrations",
            table: "provider_connections",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections",
            sql: "status IN ('Active','Degraded','RequiresReconnect','NotEntitled','QuotaExhausted','Invalid','Revoked')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.DropColumn(
            name: "external_account_id",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.DropColumn(
            name: "external_account_login",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.Sql(
            "UPDATE integrations.provider_connections SET status = 'Invalid' " +
            "WHERE status NOT IN ('Active','Invalid','Revoked')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_status",
            schema: "integrations",
            table: "provider_connections",
            sql: "status IN ('Active','Invalid','Revoked')");
    }
}
