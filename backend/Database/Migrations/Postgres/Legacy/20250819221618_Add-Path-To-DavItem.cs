using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class AddPathToDavItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Path",
                table: "DavItems",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Populate the Path column for every existing DavItem
            // * The root DavItem is given path `/`
            // * Every other DavItem is given path `{PARENT_PATH}/{NAME}`
            migrationBuilder.Sql(
                """
                WITH RECURSIVE computed("Id", "Path") AS (
                    SELECT "Id", '/'
                    FROM "DavItems"
                    WHERE "Id" = '00000000-0000-0000-0000-000000000000'

                    UNION ALL

                    SELECT
                        d."Id",
                        CASE
                            WHEN c."Path" = '/' THEN '/' || d."Name"
                            ELSE c."Path" || '/' || d."Name"
                        END AS "Path"
                    FROM "DavItems" AS d
                    JOIN computed AS c ON d."ParentId" = c."Id"
                )

                UPDATE "DavItems" AS items
                SET "Path" = (SELECT computed."Path" FROM computed WHERE items."Id" = computed."Id");
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Path",
                table: "DavItems");
        }
    }
}
