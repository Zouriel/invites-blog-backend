using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaBucketMembersAndWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_media_buckets_campaign",
                table: "media_buckets");

            // ---- repair before constraining ----
            //
            // A bucket with no event behind it is a row from before a bucket bought on its own
            // started getting a bare campaign of its own. Production holds none; a development
            // database restored from that week holds a few, and they are not debris — the one this
            // was written against had two photographs and three printed QR codes hanging off it.
            //
            // So they are REPAIRED rather than deleted or defaulted, by doing exactly what
            // CampaignService.CreateBareAsync does for a new one: a placeholder template marked
            // Imported, which every gallery read already fails to match and so is invisible
            // everywhere a template would be listed, and a campaign pinned to it carrying the
            // bucket's own night. The photographs and the codes survive, and the bucket gains the
            // title and guest list it always read from an event it did not have.
            //
            // The access token is random and unrecorded ON PURPOSE. A campaign's token is a bearer
            // credential and nobody holds one for a campaign invented by a migration; the owner
            // reaches this through owner_user_id, which is how they reached the bucket already.
            migrationBuilder.Sql("""
                WITH orphan AS (
                    SELECT id, event_date, created_at, gen_random_uuid() AS template_id,
                           gen_random_uuid() AS campaign_id
                    FROM media_buckets
                    WHERE campaign_id IS NULL
                ),
                new_template AS (
                    INSERT INTO templates (
                        id, name, slug, version, category, description, preview_image_url,
                        is_premium, scene_json, manifest_json, package_url, is_active,
                        visibility, created_at, updated_at)
                    SELECT o.template_id, 'Media bucket', 'bare-' || replace(o.template_id::text, '-', ''),
                           '1.0.0', 'Imported', 'A campaign with no invitation.', '',
                           false, '{}', '{"fields":[],"images":[],"blocks":[],"theme":{}}', '', true,
                           'Imported', now(), now()
                    FROM orphan o
                    RETURNING id
                ),
                new_campaign AS (
                    INSERT INTO campaigns (
                        id, template_id, template_version, access_token_hash, title, slug, status,
                        event_type, event_start_at, paid_invite_capacity, has_designer_discount,
                        is_sensitive, retention_days, custom_content_json, theme_overrides_json,
                        delivery_settings_json, rules_json, created_at, updated_at)
                    SELECT o.campaign_id, o.template_id, '1.0.0',
                           encode(sha256(gen_random_uuid()::text::bytea), 'hex'),
                           'Media bucket', 'bucket-' || replace(o.campaign_id::text, '-', ''),
                           0, 'other', o.event_date, 0, false, false, 365,
                           '{}', '{}', '{}', '{}', o.created_at, now()
                    FROM orphan o
                    RETURNING id
                )
                UPDATE media_buckets b
                SET campaign_id = o.campaign_id
                FROM orphan o
                WHERE b.id = o.id;
                """);

            // Deliberately WITHOUT a default. EF scaffolds an empty guid here, which is exactly the
            // sentinel this schema refuses elsewhere — "a value every campaign query has to know to
            // skip". Nothing has been able to write a null since a bucket without an event started
            // getting a bare campaign of its own, and production holds none; if one ever did appear,
            // SET NOT NULL failing loudly is a far better outcome than six buckets quietly belonging
            // to an event that does not exist.
            migrationBuilder.AlterColumn<Guid>(
                name: "campaign_id",
                table: "media_buckets",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_restricted",
                table: "media_buckets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // ONE, not the zero EF scaffolds from the CLR default: this column says how many days a
            // bucket accepts uploads for, and backfilling every existing bucket with zero would shut
            // all of them on the spot.
            migrationBuilder.AddColumn<int>(
                name: "upload_window_days",
                table: "media_buckets",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_media_buckets_campaign_id_id",
                table: "media_buckets",
                columns: new[] { "campaign_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_guests_campaign_id_id",
                table: "guests",
                columns: new[] { "campaign_id", "id" });

            migrationBuilder.CreateTable(
                name: "media_bucket_members",
                columns: table => new
                {
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_bucket_members", x => new { x.bucket_id, x.guest_id });
                    table.ForeignKey(
                        name: "FK_media_bucket_members_guests_campaign_id_guest_id",
                        columns: x => new { x.campaign_id, x.guest_id },
                        principalTable: "guests",
                        principalColumns: new[] { "campaign_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_bucket_members_media_buckets_campaign_id_bucket_id",
                        columns: x => new { x.campaign_id, x.bucket_id },
                        principalTable: "media_buckets",
                        principalColumns: new[] { "campaign_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_media_buckets_campaign",
                table: "media_buckets",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "idx_media_buckets_campaign_id",
                table: "media_buckets",
                columns: new[] { "campaign_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_guests_campaign_id_id",
                table: "guests",
                columns: new[] { "campaign_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_bucket_members_campaign_id_bucket_id",
                table: "media_bucket_members",
                columns: new[] { "campaign_id", "bucket_id" });

            migrationBuilder.CreateIndex(
                name: "IX_media_bucket_members_campaign_id_guest_id",
                table: "media_bucket_members",
                columns: new[] { "campaign_id", "guest_id" });

            migrationBuilder.CreateIndex(
                name: "IX_media_bucket_members_guest_id",
                table: "media_bucket_members",
                column: "guest_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_bucket_members");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_media_buckets_campaign_id_id",
                table: "media_buckets");

            migrationBuilder.DropIndex(
                name: "idx_media_buckets_campaign",
                table: "media_buckets");

            migrationBuilder.DropIndex(
                name: "idx_media_buckets_campaign_id",
                table: "media_buckets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_guests_campaign_id_id",
                table: "guests");

            migrationBuilder.DropIndex(
                name: "idx_guests_campaign_id_id",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "is_restricted",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "upload_window_days",
                table: "media_buckets");

            migrationBuilder.AlterColumn<Guid>(
                name: "campaign_id",
                table: "media_buckets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "idx_media_buckets_campaign",
                table: "media_buckets",
                column: "campaign_id",
                unique: true,
                filter: "campaign_id IS NOT NULL");
        }
    }
}
