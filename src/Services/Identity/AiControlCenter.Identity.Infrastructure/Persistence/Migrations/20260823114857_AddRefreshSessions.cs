using Microsoft.EntityFrameworkCore.Migrations;

namespace AiControlCenter.Identity.Infrastructure.Persistence.Migrations;

public partial class AddRefreshSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    GROUP BY family_id
                    HAVING count(DISTINCT user_id) > 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy family_id belongs to multiple distinct users.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens AS source
                    LEFT JOIN identity.refresh_tokens AS replacement
                        ON replacement.id = source.replaced_by_token_id
                    WHERE source.replaced_by_token_id IS NOT NULL
                      AND replacement.id IS NULL
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy replacement token does not exist.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    WHERE id = replaced_by_token_id
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy replacement graph contains a self-reference.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens AS source
                    INNER JOIN identity.refresh_tokens AS replacement
                        ON replacement.id = source.replaced_by_token_id
                    WHERE source.user_id <> replacement.user_id
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: linked legacy replacement tokens belong to different users.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens AS source
                    INNER JOIN identity.refresh_tokens AS replacement
                        ON replacement.id = source.replaced_by_token_id
                    WHERE source.family_id <> replacement.family_id
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: linked legacy replacement tokens belong to different refresh families.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    GROUP BY family_id
                    HAVING count(*) FILTER (
                        WHERE revoked_at IS NULL
                          AND replaced_by_token_id IS NULL) > 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy family_id contains multiple current tokens.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    WHERE replaced_by_token_id IS NOT NULL
                      AND revoked_at IS NULL
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy replacement link has no revocation timestamp.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    WHERE replaced_by_token_id IS NOT NULL
                      AND revocation_reason IS DISTINCT FROM 'Rotated'
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy replacement source is not in the Rotated state.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    WHERE replaced_by_token_id IS NOT NULL
                    GROUP BY replaced_by_token_id
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy replacement graph contains branching.';
                END IF;

                IF EXISTS (
                    WITH RECURSIVE replacement_walk AS (
                        SELECT
                            token.id AS start_id,
                            token.id AS current_id,
                            token.replaced_by_token_id AS next_id,
                            ARRAY[token.id] AS visited,
                            false AS cycle_detected
                        FROM identity.refresh_tokens AS token

                        UNION ALL

                        SELECT
                            walk.start_id,
                            replacement.id,
                            replacement.replaced_by_token_id,
                            walk.visited || replacement.id,
                            replacement.id = ANY(walk.visited)
                        FROM replacement_walk AS walk
                        INNER JOIN identity.refresh_tokens AS replacement
                            ON replacement.id = walk.next_id
                        WHERE walk.next_id IS NOT NULL
                          AND NOT walk.cycle_detected
                    )
                    SELECT 1
                    FROM replacement_walk
                    WHERE cycle_detected
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy replacement graph contains a cycle.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    GROUP BY family_id
                    HAVING count(*) FILTER (WHERE replaced_by_token_id IS NULL) <> 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: each legacy family must contain exactly one terminal token.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    GROUP BY family_id
                    HAVING count(DISTINCT expires_at) > 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy family has inconsistent expiration timestamps.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens AS source
                    INNER JOIN identity.refresh_tokens AS replacement
                        ON replacement.id = source.replaced_by_token_id
                    WHERE source.revoked_at < source.created_at
                       OR source.revoked_at <> replacement.created_at
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy replacement edge has inconsistent rotation timestamps.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM identity.refresh_tokens
                    WHERE expires_at <= created_at
                       OR (revoked_at IS NOT NULL AND revoked_at < created_at)
                       OR (revoked_at IS NULL AND revocation_reason IS NOT NULL)
                       OR (revoked_at IS NOT NULL AND nullif(btrim(revocation_reason), '') IS NULL)
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'RefreshSession migration preflight failed: a legacy token has an inconsistent lifecycle state.';
                END IF;
            END
            $migration$;
            """);

        migrationBuilder.CreateTable(
            name: "refresh_sessions",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                revoked_reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_refresh_sessions", session => session.id);
                table.ForeignKey(
                    name: "fk_refresh_sessions_users_user_id",
                    column: session => session.user_id,
                    principalSchema: "identity",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql(
            """
            INSERT INTO identity.refresh_sessions
                (id, user_id, created_at, last_used_at, expires_at, revoked_at, revoked_reason)
            SELECT
                family_id,
                (array_agg(user_id ORDER BY created_at))[1],
                min(created_at),
                max(coalesce(revoked_at, created_at)),
                min(expires_at),
                CASE
                    WHEN bool_or(revoked_at IS NULL) THEN NULL
                    ELSE max(revoked_at) FILTER (WHERE replaced_by_token_id IS NULL)
                END,
                CASE
                    WHEN bool_or(revoked_at IS NULL) THEN NULL
                    ELSE coalesce(
                        (array_agg(revocation_reason)
                            FILTER (WHERE replaced_by_token_id IS NULL))[1],
                        'Migrated revoked session')
                END
            FROM identity.refresh_tokens
            GROUP BY family_id;
            """);

        migrationBuilder.AddColumn<Guid>(
            name: "session_id",
            schema: "identity",
            table: "refresh_tokens",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "used_at",
            schema: "identity",
            table: "refresh_tokens",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE identity.refresh_tokens
            SET session_id = family_id,
                used_at = CASE WHEN replaced_by_token_id IS NOT NULL THEN revoked_at ELSE NULL END;
            """);

        migrationBuilder.AlterColumn<Guid>(
            name: "session_id",
            schema: "identity",
            table: "refresh_tokens",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.DropForeignKey(
            name: "fk_refresh_tokens_users_user_id",
            schema: "identity",
            table: "refresh_tokens");
        migrationBuilder.DropIndex(
            name: "ix_refresh_tokens_family_id",
            schema: "identity",
            table: "refresh_tokens");
        migrationBuilder.DropIndex(
            name: "ix_refresh_tokens_user_expiration",
            schema: "identity",
            table: "refresh_tokens");
        migrationBuilder.DropColumn(name: "user_id", schema: "identity", table: "refresh_tokens");
        migrationBuilder.DropColumn(name: "family_id", schema: "identity", table: "refresh_tokens");
        migrationBuilder.DropColumn(name: "revoked_at", schema: "identity", table: "refresh_tokens");
        migrationBuilder.DropColumn(name: "revocation_reason", schema: "identity", table: "refresh_tokens");

        migrationBuilder.CreateIndex(
            name: "ix_refresh_sessions_user_expiration",
            schema: "identity",
            table: "refresh_sessions",
            columns: ["user_id", "expires_at"]);
        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_session_id",
            schema: "identity",
            table: "refresh_tokens",
            column: "session_id");
        migrationBuilder.AddForeignKey(
            name: "fk_refresh_tokens_refresh_sessions_session_id",
            schema: "identity",
            table: "refresh_tokens",
            column: "session_id",
            principalSchema: "identity",
            principalTable: "refresh_sessions",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "user_id",
            schema: "identity",
            table: "refresh_tokens",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "family_id",
            schema: "identity",
            table: "refresh_tokens",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "revoked_at",
            schema: "identity",
            table: "refresh_tokens",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "revocation_reason",
            schema: "identity",
            table: "refresh_tokens",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE identity.refresh_tokens AS token
            SET user_id = session.user_id,
                family_id = session.id,
                revoked_at = coalesce(
                    token.used_at,
                    CASE WHEN token.replaced_by_token_id IS NULL THEN session.revoked_at ELSE NULL END),
                revocation_reason = CASE
                    WHEN token.used_at IS NOT NULL THEN 'Rotated'
                    WHEN token.replaced_by_token_id IS NULL THEN session.revoked_reason
                    ELSE NULL
                END
            FROM identity.refresh_sessions AS session
            WHERE session.id = token.session_id;
            """);

        migrationBuilder.AlterColumn<Guid>(
            name: "user_id",
            schema: "identity",
            table: "refresh_tokens",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);
        migrationBuilder.AlterColumn<Guid>(
            name: "family_id",
            schema: "identity",
            table: "refresh_tokens",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.DropForeignKey(
            name: "fk_refresh_tokens_refresh_sessions_session_id",
            schema: "identity",
            table: "refresh_tokens");
        migrationBuilder.DropIndex(
            name: "ix_refresh_tokens_session_id",
            schema: "identity",
            table: "refresh_tokens");
        migrationBuilder.DropColumn(name: "session_id", schema: "identity", table: "refresh_tokens");
        migrationBuilder.DropColumn(name: "used_at", schema: "identity", table: "refresh_tokens");

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_family_id",
            schema: "identity",
            table: "refresh_tokens",
            column: "family_id");
        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_user_expiration",
            schema: "identity",
            table: "refresh_tokens",
            columns: ["user_id", "expires_at"]);
        migrationBuilder.AddForeignKey(
            name: "fk_refresh_tokens_users_user_id",
            schema: "identity",
            table: "refresh_tokens",
            column: "user_id",
            principalSchema: "identity",
            principalTable: "users",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.DropTable(name: "refresh_sessions", schema: "identity");
    }
}
