using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <summary>
    /// Corrects the column types for the yEnc layout metadata added in
    /// 20260418120000_AddYencLayoutMetadata. That migration used
    /// <c>type: "INTEGER"</c> for every column, which SQLite accepts but
    /// Npgsql interpreted as Postgres <c>integer</c> (32-bit) — wrong for
    /// <c>long?</c> and <c>bool?</c> CLR types. Every insert into DavItems
    /// therefore failed on Postgres with:
    ///   "column YencLayoutUniform is of type integer but expression is of type boolean"
    /// breaking new NZB ingest from Radarr/Sonarr and blocking the yEnc
    /// fast-path populator.
    ///
    /// This migration is a no-op on SQLite (INTEGER is dynamically typed
    /// there; every value round-trips fine). On Postgres it converts the
    /// three affected columns to their correct types.
    /// </summary>
    public partial class FixYencLayoutColumnTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Postgres-only — SQLite's INTEGER affinity handles long/bool dynamically
            // and the AddYencLayoutMetadata migration works as-is there.
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            migrationBuilder.Sql(@"
                ALTER TABLE ""DavItems""
                    ALTER COLUMN ""YencPartSize"" TYPE bigint
                    USING ""YencPartSize""::bigint;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""DavItems""
                    ALTER COLUMN ""YencLastPartSize"" TYPE bigint
                    USING ""YencLastPartSize""::bigint;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""DavItems""
                    ALTER COLUMN ""YencLayoutUniform"" TYPE boolean
                    USING (""YencLayoutUniform"" <> 0);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Postgres-only — SQLite's INTEGER affinity handles long/bool dynamically
            // and the AddYencLayoutMetadata migration works as-is there.
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            migrationBuilder.Sql(@"
                ALTER TABLE ""DavItems""
                    ALTER COLUMN ""YencPartSize"" TYPE integer
                    USING ""YencPartSize""::integer;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""DavItems""
                    ALTER COLUMN ""YencLastPartSize"" TYPE integer
                    USING ""YencLastPartSize""::integer;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""DavItems""
                    ALTER COLUMN ""YencLayoutUniform"" TYPE integer
                    USING (CASE WHEN ""YencLayoutUniform"" THEN 1 ELSE 0 END);
            ");
        }
    }
}
