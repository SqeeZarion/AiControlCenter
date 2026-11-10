using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IntegrationsDbContext))]
[Migration("20261028120000_AddLocalCopilotCliProvider")]
public partial class AddLocalCopilotCliProvider : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_provider",
            schema: "integrations",
            table: "provider_connections");
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_secret",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_provider",
            schema: "integrations",
            table: "provider_connections",
            sql: "provider IN ('GitHubCopilot','GitHubCopilotLocalCli','OpenAi','XAi','DeterministicTest')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_secret",
            schema: "integrations",
            table: "provider_connections",
            sql: "(provider IN ('DeterministicTest','GitHubCopilotLocalCli')) OR (status = 'Revoked') OR protected_credential IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM integrations.provider_execution_receipts
            WHERE provider = 'GitHubCopilotLocalCli';

            DELETE FROM integrations.provider_connections
            WHERE provider = 'GitHubCopilotLocalCli';
            """);

        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_provider",
            schema: "integrations",
            table: "provider_connections");
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_connections_secret",
            schema: "integrations",
            table: "provider_connections");

        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_provider",
            schema: "integrations",
            table: "provider_connections",
            sql: "provider IN ('GitHubCopilot','OpenAi','XAi','DeterministicTest')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_connections_secret",
            schema: "integrations",
            table: "provider_connections",
            sql: "(provider = 'DeterministicTest') OR (status = 'Revoked') OR protected_credential IS NOT NULL");
    }
}
