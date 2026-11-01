using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGooglePlayPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "target",
                table: "pending_removals",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<bool>(
                name: "is_test",
                table: "payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "period_ends_at",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "google_play_purchases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_token = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    product_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_test = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    subscription_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    auto_renewing = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_play_purchases", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_payments_google_play_order",
                table: "payments",
                column: "provider_payment_id",
                unique: true,
                filter: "provider = 'google_play' AND provider_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_google_play_purchases_user_id",
                table: "google_play_purchases",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_google_play_purchases_token",
                table: "google_play_purchases",
                column: "purchase_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "google_play_purchases");

            migrationBuilder.DropIndex(
                name: "ux_payments_google_play_order",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "is_test",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "period_ends_at",
                table: "payments");

            migrationBuilder.AlterColumn<string>(
                name: "target",
                table: "pending_removals",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);
        }
    }
}
