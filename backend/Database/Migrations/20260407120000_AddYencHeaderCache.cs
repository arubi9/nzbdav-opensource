using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    [Migration("20260407120000_AddYencHeaderCache")]
    public partial class AddYencHeaderCache : Migration
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "10.0.4");

            modelBuilder.Entity("NzbWebDAV.Database.Models.YencHeaderCacheEntry", b =>
                {
                    b.Property<string>("SegmentId")
                        .HasColumnType("TEXT");

                    b.Property<string>("FileName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<long>("FileSize")
                        .HasColumnType("BIGINT");

                    b.Property<int>("LineLength")
                        .HasColumnType("INTEGER");

                    b.Property<int>("PartNumber")
                        .HasColumnType("INTEGER");

                    b.Property<int>("TotalParts")
                        .HasColumnType("INTEGER");

                    b.Property<long>("PartSize")
                        .HasColumnType("BIGINT");

                    b.Property<long>("PartOffset")
                        .HasColumnType("BIGINT");

                    b.Property<DateTime>("CachedAt")
                        .HasColumnType("TIMESTAMPTZ")
                        .HasDefaultValueSql("now()");

                    b.HasKey("SegmentId");

                    b.HasIndex("CachedAt");

                    b.ToTable("yenc_header_cache", (string)null);
                });
#pragma warning restore 612, 618
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "yenc_header_cache",
                columns: table => new
                {
                    segment_id = table.Column<string>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", nullable: false),
                    file_size = table.Column<long>(type: "BIGINT", nullable: false),
                    line_length = table.Column<int>(type: "INTEGER", nullable: false),
                    part_number = table.Column<int>(type: "INTEGER", nullable: false),
                    total_parts = table.Column<int>(type: "INTEGER", nullable: false),
                    part_size = table.Column<long>(type: "BIGINT", nullable: false),
                    part_offset = table.Column<long>(type: "BIGINT", nullable: false),
                    cached_at = table.Column<DateTime>(type: "TIMESTAMPTZ", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_yenc_header_cache", x => x.segment_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_yenc_header_cache_cached_at",
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
