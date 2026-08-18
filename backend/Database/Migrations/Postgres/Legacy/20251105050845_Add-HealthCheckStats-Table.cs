using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class AddHealthCheckStatsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthCheckStats",
                columns: table => new
                {
                    DateStartInclusive = table.Column<long>(type: "bigint", nullable: false),
                    DateEndExclusive = table.Column<long>(type: "bigint", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    RepairStatus = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckStats", x => new { x.DateStartInclusive, x.DateEndExclusive, x.Result, x.RepairStatus });
                });

            // Populate the daily rollup from the epoch-second CreatedAt values.
            migrationBuilder.Sql(
                """
                INSERT INTO "HealthCheckStats" ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                SELECT
                    EXTRACT(EPOCH FROM day_start)::bigint,
                    EXTRACT(EPOCH FROM (day_start + INTERVAL '1 day'))::bigint,
                    "Result",
                    "RepairStatus",
                    COUNT(*)::integer
                FROM (
                    SELECT
                        date_trunc('day', to_timestamp("CreatedAt") AT TIME ZONE 'UTC') AS day_start,
                        "Result",
                        "RepairStatus"
                    FROM "HealthCheckResults"
                ) AS daily
                GROUP BY day_start, "Result", "RepairStatus";
                """
            );

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "TR_HealthCheckResults_IncrementStats"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    day_start bigint;
                    day_end bigint;
                BEGIN
                    day_start := EXTRACT(EPOCH FROM date_trunc('day', to_timestamp(NEW."CreatedAt") AT TIME ZONE 'UTC'))::bigint;
                    day_end := day_start + 86400;
                    INSERT INTO "HealthCheckStats" ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                    VALUES (day_start, day_end, NEW."Result", NEW."RepairStatus", 1)
                    ON CONFLICT ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus")
                    DO UPDATE SET "Count" = "HealthCheckStats"."Count" + 1;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_HealthCheckResults_IncrementStats"
                AFTER INSERT ON "HealthCheckResults"
                FOR EACH ROW EXECUTE FUNCTION "TR_HealthCheckResults_IncrementStats"();
                """
            );

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "TR_HealthCheckResults_DecrementStats"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    day_start bigint;
                    day_end bigint;
                BEGIN
                    day_start := EXTRACT(EPOCH FROM date_trunc('day', to_timestamp(OLD."CreatedAt") AT TIME ZONE 'UTC'))::bigint;
                    day_end := day_start + 86400;
                    UPDATE "HealthCheckStats"
                    SET "Count" = "Count" - 1
                    WHERE "DateStartInclusive" = day_start
                      AND "DateEndExclusive" = day_end
                      AND "Result" = OLD."Result"
                      AND "RepairStatus" = OLD."RepairStatus";
                    DELETE FROM "HealthCheckStats"
                    WHERE "DateStartInclusive" = day_start
                      AND "DateEndExclusive" = day_end
                      AND "Result" = OLD."Result"
                      AND "RepairStatus" = OLD."RepairStatus"
                      AND "Count" <= 0;
                    RETURN OLD;
                END;
                $$;

                CREATE TRIGGER "TR_HealthCheckResults_DecrementStats"
                AFTER DELETE ON "HealthCheckResults"
                FOR EACH ROW EXECUTE FUNCTION "TR_HealthCheckResults_DecrementStats"();
                """
            );

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "TR_HealthCheckResults_UpdateStats"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    old_day_start bigint;
                    old_day_end bigint;
                    new_day_start bigint;
                    new_day_end bigint;
                BEGIN
                    old_day_start := EXTRACT(EPOCH FROM date_trunc('day', to_timestamp(OLD."CreatedAt") AT TIME ZONE 'UTC'))::bigint;
                    old_day_end := old_day_start + 86400;
                    new_day_start := EXTRACT(EPOCH FROM date_trunc('day', to_timestamp(NEW."CreatedAt") AT TIME ZONE 'UTC'))::bigint;
                    new_day_end := new_day_start + 86400;

                    UPDATE "HealthCheckStats"
                    SET "Count" = "Count" - 1
                    WHERE "DateStartInclusive" = old_day_start
                      AND "DateEndExclusive" = old_day_end
                      AND "Result" = OLD."Result"
                      AND "RepairStatus" = OLD."RepairStatus";
                    DELETE FROM "HealthCheckStats"
                    WHERE "DateStartInclusive" = old_day_start
                      AND "DateEndExclusive" = old_day_end
                      AND "Result" = OLD."Result"
                      AND "RepairStatus" = OLD."RepairStatus"
                      AND "Count" <= 0;

                    INSERT INTO "HealthCheckStats" ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                    VALUES (new_day_start, new_day_end, NEW."Result", NEW."RepairStatus", 1)
                    ON CONFLICT ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus")
                    DO UPDATE SET "Count" = "HealthCheckStats"."Count" + 1;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_HealthCheckResults_UpdateStats"
                AFTER UPDATE OF "Result", "RepairStatus" ON "HealthCheckResults"
                FOR EACH ROW
                WHEN (OLD."Result" IS DISTINCT FROM NEW."Result" OR OLD."RepairStatus" IS DISTINCT FROM NEW."RepairStatus")
                EXECUTE FUNCTION "TR_HealthCheckResults_UpdateStats"();
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_HealthCheckResults_UpdateStats" ON "HealthCheckResults";
                DROP TRIGGER IF EXISTS "TR_HealthCheckResults_DecrementStats" ON "HealthCheckResults";
                DROP TRIGGER IF EXISTS "TR_HealthCheckResults_IncrementStats" ON "HealthCheckResults";
                DROP FUNCTION IF EXISTS "TR_HealthCheckResults_UpdateStats"();
                DROP FUNCTION IF EXISTS "TR_HealthCheckResults_DecrementStats"();
                DROP FUNCTION IF EXISTS "TR_HealthCheckResults_IncrementStats"();
                """
            );

            migrationBuilder.DropTable(
                name: "HealthCheckStats");
        }
    }
}
