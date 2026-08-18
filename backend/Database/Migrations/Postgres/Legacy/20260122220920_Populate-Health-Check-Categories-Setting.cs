using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class PopulateHealthCheckCategoriesSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Combine api.manual-category with api.categories when article existence is enabled.
            migrationBuilder.Sql(
                """
                WITH settings AS (
                    SELECT
                        COALESCE(
                            (SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = 'api.ensure-article-existence'),
                            'false'
                        ) AS ensure_existence,
                        COALESCE(
                            (SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = 'api.categories'),
                            'audio, software, tv, movies'
                        ) AS categories,
                        COALESCE(
                            (SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = 'api.manual-category'),
                            'uncategorized'
                        ) AS manual_category
                ),
                category_list AS (
                    SELECT trim(split.item) AS item
                    FROM settings
                    CROSS JOIN LATERAL regexp_split_to_table(settings.categories, ',') AS split(item)
                    WHERE lower(settings.ensure_existence) = 'true'
                      AND trim(split.item) <> ''
                ),
                combined AS (
                    SELECT trim(settings.manual_category) || ', ' || string_agg(category_list.item, ', ') AS combined_categories
                    FROM settings
                    CROSS JOIN category_list
                    WHERE lower(settings.ensure_existence) = 'true'
                    GROUP BY settings.manual_category
                )
                INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
                SELECT 'api.ensure-article-existence-categories', combined_categories
                FROM combined
                WHERE combined_categories IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "ConfigItems"
                      WHERE "ConfigName" = 'api.ensure-article-existence-categories'
                  );
                """
            );

            migrationBuilder.Sql(
                """
                DELETE FROM "ConfigItems"
                WHERE "ConfigName" = 'api.ensure-article-existence';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
                SELECT 'api.ensure-article-existence', 'true'
                WHERE EXISTS (
                    SELECT 1 FROM "ConfigItems"
                    WHERE "ConfigName" = 'api.ensure-article-existence-categories'
                      AND trim("ConfigValue") <> ''
                )
                AND NOT EXISTS (
                    SELECT 1 FROM "ConfigItems"
                    WHERE "ConfigName" = 'api.ensure-article-existence'
                );
                """
            );

            migrationBuilder.Sql(
                """
                DELETE FROM "ConfigItems"
                WHERE "ConfigName" = 'api.ensure-article-existence-categories';
                """
            );
        }
    }
}
