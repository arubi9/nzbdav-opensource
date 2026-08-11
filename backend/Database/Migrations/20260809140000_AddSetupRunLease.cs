using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSetupRunLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "setup_run_leases",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", nullable: false),
                    grant_hash = table.Column<string>(type: "TEXT", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", nullable: false),
                    generation = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_until_utc = table.Column<DateTime>(type: "TIMESTAMP WITH TIME ZONE", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_run_leases", x => x.id);
                    table.CheckConstraint("CK_setup_run_leases_singleton", "\"id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setup_run_leases");
        }
    }
}
