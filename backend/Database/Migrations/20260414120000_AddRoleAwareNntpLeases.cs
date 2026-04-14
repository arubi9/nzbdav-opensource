using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    public partial class AddRoleAwareNntpLeases : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nntp_node_heartbeats",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_index = table.Column<int>(type: "INTEGER", nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false),
                    region = table.Column<string>(type: "TEXT", nullable: false),
                    desired_slots = table.Column<int>(type: "INTEGER", nullable: false),
                    active_slots = table.Column<int>(type: "INTEGER", nullable: false),
                    live_slots = table.Column<int>(type: "INTEGER", nullable: false),
                    has_demand = table.Column<bool>(type: "INTEGER", nullable: false),
                    heartbeat_at = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nntp_node_heartbeats", x => new { x.node_id, x.provider_index });
                });

            migrationBuilder.CreateIndex(
                name: "ix_nntp_node_heartbeats_heartbeat_at",
                table: "nntp_node_heartbeats",
                column: "heartbeat_at");

            migrationBuilder.CreateTable(
                name: "nntp_connection_leases",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_index = table.Column<int>(type: "INTEGER", nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false),
                    reserved_slots = table.Column<int>(type: "INTEGER", nullable: false),
                    borrowed_slots = table.Column<int>(type: "INTEGER", nullable: false),
                    granted_slots = table.Column<int>(type: "INTEGER", nullable: false),
                    epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nntp_connection_leases", x => new { x.node_id, x.provider_index });
                });

            migrationBuilder.CreateIndex(
                name: "ix_nntp_connection_leases_lease_until",
                table: "nntp_connection_leases",
                column: "lease_until");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nntp_connection_leases");

            migrationBuilder.DropTable(
                name: "nntp_node_heartbeats");
        }
    }
}
