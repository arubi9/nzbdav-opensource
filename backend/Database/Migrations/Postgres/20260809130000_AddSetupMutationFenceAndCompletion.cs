using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSetupMutationFenceAndCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "setup_completion_operations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revocation_pending = table.Column<bool>(type: "boolean", nullable: false),
                    active_session_ciphertext = table.Column<string>(type: "text", nullable: true),
                    revocation_session_ciphertext = table.Column<string>(type: "text", nullable: true),
                    candidate_session_ciphertext = table.Column<string>(type: "text", nullable: true),
                    candidate_operation_ciphertext = table.Column<string>(type: "text", nullable: true),
                    emergency_session_ciphertext = table.Column<string>(type: "text", nullable: true),
                    emergency_operation_ciphertext = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_completion_operations", x => x.id);
                    table.CheckConstraint("CK_setup_completion_operations_singleton", "\"id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "setup_mutation_fence",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    reserved_candidate_operation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_mutation_fence", x => x.id);
                    table.CheckConstraint("CK_setup_mutation_fence_singleton", "\"id\" = 1");
                });

            migrationBuilder.Sql("INSERT INTO \"setup_mutation_fence\" (\"id\", \"epoch\") VALUES (1, 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setup_completion_operations");

            migrationBuilder.DropTable(
                name: "setup_mutation_fence");
        }
    }
}
