using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable IDE0161

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderConnectionMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "available_models_json",
                schema: "integrations",
                table: "provider_connections",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "credential_version",
                schema: "integrations",
                table: "provider_connections",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "last_error_category",
                schema: "integrations",
                table: "provider_connections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_safe_error",
                schema: "integrations",
                table: "provider_connections",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_synced_at",
                schema: "integrations",
                table: "provider_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quota_source",
                schema: "integrations",
                table: "provider_connections",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.AddColumn<string>(
                name: "quota_state",
                schema: "integrations",
                table: "provider_connections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_connections_credential_version",
                schema: "integrations",
                table: "provider_connections",
                sql: "credential_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_connections_quota_source",
                schema: "integrations",
                table: "provider_connections",
                sql: "quota_source IN ('ProviderReported','Estimated','Unavailable')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_connections_credential_version",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_connections_quota_source",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "available_models_json",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "credential_version",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "last_error_category",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "last_safe_error",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "last_synced_at",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "quota_source",
                schema: "integrations",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "quota_state",
                schema: "integrations",
                table: "provider_connections");
        }
    }
}
