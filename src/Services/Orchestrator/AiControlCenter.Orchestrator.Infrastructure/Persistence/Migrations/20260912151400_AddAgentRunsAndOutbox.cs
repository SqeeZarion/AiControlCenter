using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable
#pragma warning disable CA1861

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAgentRunsAndOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "orchestrator");

        migrationBuilder.CreateTable(
            name: "agent_runs",
            schema: "orchestrator",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                agent_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                agent_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                agent_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                agent_version = table.Column<long>(type: "bigint", nullable: false),
                direction_id = table.Column<Guid>(type: "uuid", nullable: false),
                direction_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                direction_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                direction_version = table.Column<long>(type: "bigint", nullable: false),
                execution_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                input = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                expected_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                execution_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                revision = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                result = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_runs", x => x.id);
                table.CheckConstraint("ck_agent_runs_execution_type", "execution_type = 'Test'");
                table.CheckConstraint("ck_agent_runs_expected_outcome", "expected_outcome IN ('Succeed','Fail')");
                table.CheckConstraint("ck_agent_runs_lifecycle", "(status = 'Queued' AND started_at IS NULL AND completed_at IS NULL) OR (status = 'Running' AND started_at IS NOT NULL AND completed_at IS NULL) OR (status = 'Succeeded' AND started_at IS NOT NULL AND completed_at IS NOT NULL) OR (status = 'Failed' AND completed_at IS NOT NULL)");
                table.CheckConstraint("ck_agent_runs_revision", "revision >= 1");
                table.CheckConstraint("ck_agent_runs_snapshot_versions", "agent_version > 0 AND direction_version > 0");
                table.CheckConstraint("ck_agent_runs_status", "status IN ('Queued','Running','Succeeded','Failed')");
                table.CheckConstraint("ck_agent_runs_terminal_payload", "(status IN ('Queued','Running') AND result IS NULL AND error IS NULL) OR (status = 'Succeeded' AND error IS NULL) OR (status = 'Failed' AND error IS NOT NULL AND btrim(error) <> '')");
                table.CheckConstraint("ck_agent_runs_timestamps", "(started_at IS NULL OR started_at >= created_at) AND (completed_at IS NULL OR completed_at >= COALESCE(started_at, created_at))");
            });

        migrationBuilder.CreateTable(
            name: "InboxState",
            schema: "orchestrator",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ConsumerId = table.Column<Guid>(type: "uuid", nullable: false),
                LockId = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReceiveCount = table.Column<int>(type: "integer", nullable: false),
                ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxState", x => x.Id);
                table.UniqueConstraint("AK_InboxState_MessageId_ConsumerId", x => new { x.MessageId, x.ConsumerId });
            });

        migrationBuilder.CreateTable(
            name: "OutboxState",
            schema: "orchestrator",
            columns: table => new
            {
                OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                LockId = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxState", x => x.OutboxId);
            });

        migrationBuilder.CreateTable(
            name: "run_steps",
            schema: "orchestrator",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                sequence = table.Column<int>(type: "integer", nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                log = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_run_steps", x => x.id);
                table.CheckConstraint("ck_run_steps_lifecycle", "(status = 'Running' AND completed_at IS NULL) OR (status IN ('Succeeded','Failed') AND completed_at IS NOT NULL)");
                table.CheckConstraint("ck_run_steps_sequence", "sequence BETWEEN 1 AND 16");
                table.CheckConstraint("ck_run_steps_status", "status IN ('Running','Succeeded','Failed')");
                table.CheckConstraint("ck_run_steps_timestamps", "updated_at >= started_at AND (completed_at IS NULL OR completed_at >= started_at)");
                table.ForeignKey(
                    name: "fk_run_steps_agent_runs_run_id",
                    column: x => x.agent_run_id,
                    principalSchema: "orchestrator",
                    principalTable: "agent_runs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OutboxMessage",
            schema: "orchestrator",
            columns: table => new
            {
                SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EnqueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                SentTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Headers = table.Column<string>(type: "text", nullable: true),
                Properties = table.Column<string>(type: "text", nullable: true),
                InboxMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                InboxConsumerId = table.Column<Guid>(type: "uuid", nullable: true),
                OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                MessageType = table.Column<string>(type: "text", nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                InitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                SourceAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                DestinationAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ResponseAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                FaultAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                table.ForeignKey(
                    name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
                    columns: x => new { x.InboxMessageId, x.InboxConsumerId },
                    principalSchema: "orchestrator",
                    principalTable: "InboxState",
                    principalColumns: new[] { "MessageId", "ConsumerId" });
                table.ForeignKey(
                    name: "FK_OutboxMessage_OutboxState_OutboxId",
                    column: x => x.OutboxId,
                    principalSchema: "orchestrator",
                    principalTable: "OutboxState",
                    principalColumn: "OutboxId");
            });

        migrationBuilder.CreateIndex(
            name: "ix_agent_runs_owner_created",
            schema: "orchestrator",
            table: "agent_runs",
            columns: new[] { "owner_user_id", "created_at", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_agent_runs_status_created",
            schema: "orchestrator",
            table: "agent_runs",
            columns: new[] { "status", "created_at", "id" });

        migrationBuilder.CreateIndex(
            name: "ux_agent_runs_execution_message_id",
            schema: "orchestrator",
            table: "agent_runs",
            column: "execution_message_id",
            unique: true,
            filter: "execution_message_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_InboxState_Delivered",
            schema: "orchestrator",
            table: "InboxState",
            column: "Delivered");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_EnqueueTime",
            schema: "orchestrator",
            table: "OutboxMessage",
            column: "EnqueueTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_ExpirationTime",
            schema: "orchestrator",
            table: "OutboxMessage",
            column: "ExpirationTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "orchestrator",
            table: "OutboxMessage",
            columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_OutboxId_SequenceNumber",
            schema: "orchestrator",
            table: "OutboxMessage",
            columns: new[] { "OutboxId", "SequenceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxState_Created",
            schema: "orchestrator",
            table: "OutboxState",
            column: "Created");

        migrationBuilder.CreateIndex(
            name: "ux_run_steps_run_sequence",
            schema: "orchestrator",
            table: "run_steps",
            columns: new[] { "agent_run_id", "sequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OutboxMessage",
            schema: "orchestrator");

        migrationBuilder.DropTable(
            name: "run_steps",
            schema: "orchestrator");

        migrationBuilder.DropTable(
            name: "InboxState",
            schema: "orchestrator");

        migrationBuilder.DropTable(
            name: "OutboxState",
            schema: "orchestrator");

        migrationBuilder.DropTable(
            name: "agent_runs",
            schema: "orchestrator");
    }
}
