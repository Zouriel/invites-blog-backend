using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppStorePurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_store_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_store_intents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_store_purchases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    product_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_test = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    subscription_status = table.Column<int>(type: "integer", nullable: true),
                    auto_renewing = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_store_purchases", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_payments_app_store_transaction",
                table: "payments",
                column: "provider_payment_id",
                unique: true,
                filter: "provider = 'app_store' AND provider_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_app_store_intents_user_id",
                table: "app_store_intents",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_store_purchases_user_id",
                table: "app_store_purchases",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_app_store_purchases_original",
                table: "app_store_purchases",
                column: "original_transaction_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_store_intents");

            migrationBuilder.DropTable(
                name: "app_store_purchases");

            migrationBuilder.DropIndex(
                name: "ux_payments_app_store_transaction",
                table: "payments");
        }
    }
}
