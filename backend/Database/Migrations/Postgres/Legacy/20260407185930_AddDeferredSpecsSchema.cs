using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class AddDeferredSpecsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEncrypted",
                table: "ConfigItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "auth_failures",
                columns: table => new
                {
                    ip_address = table.Column<string>(type: "text", nullable: false),
                    failure_count = table.Column<int>(type: "integer", nullable: false),
                    window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_failures", x => x.ip_address);
                });

            migrationBuilder.CreateTable(
                name: "connection_pool_claims",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "text", nullable: false),
                    provider_index = table.Column<int>(type: "integer", nullable: false),
                    claimed_slots = table.Column<int>(type: "integer", nullable: false),
                    heartbeat_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_pool_claims", x => new { x.node_id, x.provider_index });
                });

            migrationBuilder.CreateTable(
                name: "websocket_outbox",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    topic = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_websocket_outbox", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "yenc_header_cache",
                columns: table => new
                {
                    segment_id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    line_length = table.Column<int>(type: "integer", nullable: false),
                    part_number = table.Column<int>(type: "integer", nullable: false),
                    total_parts = table.Column<int>(type: "integer", nullable: false),
                    part_size = table.Column<long>(type: "bigint", nullable: false),
                    part_offset = table.Column<long>(type: "bigint", nullable: false),
                    cached_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_yenc_header_cache", x => x.segment_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_failures_window_start",
                table: "auth_failures",
                column: "window_start");

            migrationBuilder.CreateIndex(
                name: "ix_connection_pool_claims_heartbeat_at",
                table: "connection_pool_claims",
                column: "heartbeat_at");

            migrationBuilder.CreateIndex(
                name: "ix_websocket_outbox_created_at",
                table: "websocket_outbox",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_yenc_header_cache_cached_at",
                table: "yenc_header_cache",
                column: "cached_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_failures");

            migrationBuilder.DropTable(
                name: "connection_pool_claims");

            migrationBuilder.DropTable(
                name: "websocket_outbox");

            migrationBuilder.DropTable(
                name: "yenc_header_cache");

            migrationBuilder.DropColumn(
                name: "IsEncrypted",
                table: "ConfigItems");
        }
    }
}
