using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class AddMissingSegmentIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MissingSegmentIds",
                columns: table => new
                {
                    SegmentId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DetectedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MissingSegmentIds", x => x.SegmentId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MissingSegmentIds");
        }
    }
}
