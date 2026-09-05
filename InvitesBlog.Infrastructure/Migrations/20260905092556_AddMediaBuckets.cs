using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "campaign_id",
                table: "event_photos",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "bucket_id",
                table: "event_photos",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media_bucket_qrs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    image_url = table.Column<string>(type: "text", nullable: false),
                    token_hint = table.Column<string>(type: "text", nullable: false),
                    allow_anonymous = table.Column<bool>(type: "boolean", nullable: false),
                    label = table.Column<string>(type: "text", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scan_count = table.Column<int>(type: "integer", nullable: false),
                    upload_count = table.Column<int>(type: "integer", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_bucket_qrs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_buckets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    cover_url = table.Column<string>(type: "text", nullable: true),
                    tier = table.Column<int>(type: "integer", nullable: false),
                    capacity_bytes = table.Column<long>(type: "bigint", nullable: false),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    term_start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    term_end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_buckets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_photos_bucket_id_deleted_at_created_at",
                table: "event_photos",
                columns: new[] { "bucket_id", "deleted_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_media_bucket_qr_token",
                table: "media_bucket_qrs",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_bucket_qrs_bucket_id_created_at",
                table: "media_bucket_qrs",
                columns: new[] { "bucket_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_media_buckets_campaign",
                table: "media_buckets",
                column: "campaign_id",
                unique: true,
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_media_buckets_owner_user_id",
                table: "media_buckets",
                column: "owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_bucket_qrs");

            migrationBuilder.DropTable(
                name: "media_buckets");

            migrationBuilder.DropIndex(
                name: "IX_event_photos_bucket_id_deleted_at_created_at",
                table: "event_photos");

            migrationBuilder.DropColumn(
                name: "bucket_id",
                table: "event_photos");

            migrationBuilder.AlterColumn<Guid>(
                name: "campaign_id",
                table: "event_photos",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
