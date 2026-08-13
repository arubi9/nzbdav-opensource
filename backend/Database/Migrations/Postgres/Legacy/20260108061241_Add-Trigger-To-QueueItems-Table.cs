using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations.Postgres.Legacy
{
    /// <inheritdoc />
    public partial class AddTriggerToQueueItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create trigger to automatically add a BlobCleanupItem when a QueueItem is deleted
            migrationBuilder.Sql(
                """
                CREATE FUNCTION "TR_QueueItems_AddBlobCleanup"() RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    INSERT INTO "BlobCleanupItems" ("Id")
                    VALUES (OLD."Id");
                    RETURN OLD;
                END;
                $$;

                CREATE TRIGGER "TR_QueueItems_AddBlobCleanup"
                AFTER DELETE ON "QueueItems"
                FOR EACH ROW EXECUTE FUNCTION "TR_QueueItems_AddBlobCleanup"();
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_QueueItems_AddBlobCleanup\" ON \"QueueItems\"; DROP FUNCTION IF EXISTS \"TR_QueueItems_AddBlobCleanup\"();");
        }
    }
}
