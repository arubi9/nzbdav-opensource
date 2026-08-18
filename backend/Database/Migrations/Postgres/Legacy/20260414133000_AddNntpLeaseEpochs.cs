using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    public partial class AddNntpLeaseEpochs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nntp_lease_epochs",
                columns: table => new
                {
                    provider_index = table.Column<int>(type: "integer", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nntp_lease_epochs", x => x.provider_index);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nntp_lease_epochs");
        }
    }
}
