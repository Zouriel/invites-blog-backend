using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlansV2StudioVenue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_premium",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "allocated_bytes",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "has_designer_discount",
                table: "campaigns");

            migrationBuilder.AddColumn<int>(
                name: "event_pass",
                table: "campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "keep_photos_until",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "venue_id",
                table: "campaigns",
                type: "uuid",
                nullable: true);

            // Every pass given before this was the one-size "event pass": 50 GB and three albums. The
            // Wedding pass is the one that covers all of that, so that is what they become.
            migrationBuilder.Sql("UPDATE campaigns SET event_pass = 2 WHERE event_pass_until IS NOT NULL;");

            // Subscription tier 1 was Basic, retired; 2 was Premium and is now Studio (same number).
            migrationBuilder.Sql("UPDATE users SET subscription_tier = 0 WHERE subscription_tier = 1;");

            migrationBuilder.CreateTable(
                name: "pass_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_on_campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pass_credits", x => x.id);
                    table.ForeignKey(
                        name: "FK_pass_credits_campaigns_used_on_campaign_id",
                        column: x => x.used_on_campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_pass_credits_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    place = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    logo_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_venues", x => x.id);
                    table.ForeignKey(
                        name: "FK_venues_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "venue_staff",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_venue_staff", x => x.id);
                    table.ForeignKey(
                        name: "FK_venue_staff_venues_venue_id",
                        column: x => x.venue_id,
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_campaigns_venue_id",
                table: "campaigns",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "idx_pass_credits_owner_user_id",
                table: "pass_credits",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_pass_credits_used_on_campaign_id",
                table: "pass_credits",
                column: "used_on_campaign_id");

            migrationBuilder.CreateIndex(
                name: "idx_venue_staff_email",
                table: "venue_staff",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "idx_venue_staff_venue_email",
                table: "venue_staff",
                columns: new[] { "venue_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_venues_owner_user_id",
                table: "venues",
                column: "owner_user_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_campaigns_venues_venue_id",
                table: "campaigns",
                column: "venue_id",
                principalTable: "venues",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_campaigns_venues_venue_id",
                table: "campaigns");

            migrationBuilder.DropTable(
                name: "pass_credits");

            migrationBuilder.DropTable(
                name: "venue_staff");

            migrationBuilder.DropTable(
                name: "venues");

            migrationBuilder.DropIndex(
                name: "idx_campaigns_venue_id",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "event_pass",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "keep_photos_until",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "venue_id",
                table: "campaigns");

            migrationBuilder.AddColumn<bool>(
                name: "is_premium",
                table: "templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "allocated_bytes",
                table: "media_buckets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_designer_discount",
                table: "campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
