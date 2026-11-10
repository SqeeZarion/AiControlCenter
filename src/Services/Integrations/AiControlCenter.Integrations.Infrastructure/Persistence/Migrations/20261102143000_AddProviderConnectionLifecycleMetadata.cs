using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IntegrationsDbContext))]
[Migration("20261102143000_AddProviderConnectionLifecycleMetadata")]
public sealed class AddProviderConnectionLifecycleMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_successful_validation_at",
            schema: "integrations",
            table: "provider_connections",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE integrations.provider_connections
            SET last_successful_validation_at = last_validated_at
            WHERE status IN ('Active', 'QuotaExhausted')
              AND last_validated_at IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "last_successful_validation_at",
            schema: "integrations",
            table: "provider_connections");
    }
}
