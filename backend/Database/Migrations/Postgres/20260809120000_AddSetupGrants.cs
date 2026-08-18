using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSetupGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "setup_grants",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    granted_token_hash = table.Column<string>(type: "text", nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_revoked = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    issued_by_username = table.Column<string>(type: "text", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false, defaultValue: "setup"),
                    repair_session_ciphertext = table.Column<string>(type: "text", nullable: true),
                    repair_session_operation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_grants", x => x.id);
                    table.CheckConstraint("CK_setup_grants_singleton", "\"id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_setup_grants_expires_at_utc",
                table: "setup_grants",
                column: "expires_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setup_grants");
        }
    }
}
