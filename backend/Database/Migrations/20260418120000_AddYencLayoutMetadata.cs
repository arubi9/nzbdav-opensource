using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddYencLayoutMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "YencPartSize",
                table: "DavItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "YencLastPartSize",
                table: "DavItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YencSegmentCount",
                table: "DavItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "YencLayoutUniform",
                table: "DavItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "YencPartSize",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "YencLastPartSize",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "YencSegmentCount",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "YencLayoutUniform",
                table: "DavItems");
        }
    }
}
