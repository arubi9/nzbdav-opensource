using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class PopulateBlocklistedFilesSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Transform api.download-extension-blacklist into api.download-file-blocklist.
            migrationBuilder.Sql(
                """
                WITH source AS (
                    SELECT "ConfigValue" AS val
                    FROM "ConfigItems"
                    WHERE "ConfigName" = 'api.download-extension-blacklist'
                ),
                transformed AS (
                    SELECT string_agg('*' || trim(item), ', ') AS new_val
                    FROM source
                    CROSS JOIN LATERAL regexp_split_to_table(source.val, ',') AS split(item)
                    WHERE trim(item) <> ''
                )
                INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
                SELECT 'api.download-file-blocklist', new_val
                FROM transformed
                WHERE new_val IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "ConfigItems"
                      WHERE "ConfigName" = 'api.download-file-blocklist'
                  );
                """
            );

            migrationBuilder.Sql(
                """
                DELETE FROM "ConfigItems"
                WHERE "ConfigName" = 'api.download-extension-blacklist';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Transform api.download-file-blocklist back to clean *.ext entries.
            migrationBuilder.Sql(
                """
                WITH source AS (
                    SELECT "ConfigValue" AS val
                    FROM "ConfigItems"
                    WHERE "ConfigName" = 'api.download-file-blocklist'
                ),
                cleaned AS (
                    SELECT substr(trim(item), 2) AS ext
                    FROM source
                    CROSS JOIN LATERAL regexp_split_to_table(source.val, ',') AS split(item)
                    WHERE trim(item) <> ''
                      AND trim(item) LIKE '*.%'
                      AND substr(trim(item), 3) NOT LIKE '%*%'
                      AND substr(trim(item), 3) NOT LIKE '%?%'
                ),
                transformed AS (
                    SELECT string_agg(ext, ', ') AS old_val
                    FROM cleaned
                )
                INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
                SELECT 'api.download-extension-blacklist', old_val
                FROM transformed
                WHERE old_val IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "ConfigItems"
                      WHERE "ConfigName" = 'api.download-extension-blacklist'
                  );
                """
            );

            migrationBuilder.Sql(
                """
                DELETE FROM "ConfigItems"
                WHERE "ConfigName" = 'api.download-file-blocklist';
                """
            );
        }
    }
}
