using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    public partial class AddYencHeaderCache : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "yenc_header_cache",
                columns: table => new
                {
                    segment_id = table.Column<string>(nullable: false),
                    file_name = table.Column<string>(nullable: false),
                    file_size = table.Column<long>(nullable: false),
                    line_length = table.Column<int>(nullable: false),
                    part_number = table.Column<int>(nullable: false),
                    total_parts = table.Column<int>(nullable: false),
                    part_size = table.Column<long>(nullable: false),
                    part_offset = table.Column<long>(nullable: false),
                    cached_at = table.Column<DateTime>(nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_yenc_header_cache", x => x.segment_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_yenc_header_cache_cached_at",
                table: "yenc_header_cache",
                column: "cached_at");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "yenc_header_cache");
        }
    }
}
