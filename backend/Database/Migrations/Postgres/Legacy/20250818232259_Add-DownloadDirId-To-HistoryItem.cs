using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class AddDownloadDirIdToHistoryItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DownloadDirId",
                table: "HistoryItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category_DownloadDirId",
                table: "HistoryItems",
                columns: new[] { "Category", "DownloadDirId" });

            // Populate DownloadDirId for completed history items based on
            // DavItems hierarchy under /content/{Category}/{JobName}
            migrationBuilder.Sql(
                """
                UPDATE "HistoryItems" AS history
                SET "DownloadDirId" = (
                  SELECT child."Id"
                  FROM "DavItems" AS child
                  JOIN "DavItems" AS parent ON child."ParentId" = parent."Id"
                  WHERE parent."ParentId" = '00000000-0000-0000-0000-000000000002'
                    AND parent."Name" = history."Category"
                    AND child."Name" = history."JobName"
                    AND child."Type" = 1
                )
                WHERE history."DownloadStatus" = 1;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HistoryItems_Category_DownloadDirId",
                table: "HistoryItems");

            migrationBuilder.DropColumn(
                name: "DownloadDirId",
                table: "HistoryItems");
        }
    }
}
