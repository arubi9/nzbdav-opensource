using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    public partial class AddMultiNodeCoordinationTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_failures",
                columns: table => new
                {
                    ip_address = table.Column<string>(nullable: false),
                    failure_count = table.Column<int>(nullable: false),
                    window_start = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_failures", x => x.ip_address);
                });

            migrationBuilder.CreateTable(
                name: "connection_pool_claims",
                columns: table => new
                {
                    node_id = table.Column<string>(nullable: false),
                    provider_index = table.Column<int>(nullable: false),
                    claimed_slots = table.Column<int>(nullable: false),
                    heartbeat_at = table.Column<DateTime>(nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_pool_claims", x => new { x.node_id, x.provider_index });
                });

            migrationBuilder.CreateTable(
                name: "websocket_outbox",
                columns: table => new
                {
                    seq = table.Column<long>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    topic = table.Column<string>(nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_websocket_outbox", x => x.seq);
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
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_failures");

            migrationBuilder.DropTable(
                name: "connection_pool_claims");

            migrationBuilder.DropTable(
                name: "websocket_outbox");
        }
    }
}
