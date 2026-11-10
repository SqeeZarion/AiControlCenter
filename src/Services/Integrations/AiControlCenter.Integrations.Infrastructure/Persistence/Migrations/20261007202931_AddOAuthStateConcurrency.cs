using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable IDE0161

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthStateConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "integrations",
                table: "provider_oauth_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "version",
                schema: "integrations",
                table: "provider_oauth_states");
        }
    }
}
