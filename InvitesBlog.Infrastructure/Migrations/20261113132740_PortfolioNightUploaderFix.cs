using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioNightUploaderFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "claim_id",
                table: "event_photos",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "portfolio_photo_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    photo_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    undone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_photo_claims", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolio_photo_claims_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_portfolio_photo_claims_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_portfolio_photo_claims_campaign_user",
                table: "portfolio_photo_claims",
                columns: new[] { "campaign_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_photo_claims_user_id",
                table: "portfolio_photo_claims",
                column: "user_id");

            // Named QR/camera uploads the evening backfill gave the organiser go back to their uploaders (see PortfolioBackfill).
            migrationBuilder.Sql(InvitesBlog.Infrastructure.Portfolios.PortfolioBackfill.NamedUploadsFixSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portfolio_photo_claims");

            migrationBuilder.DropColumn(
                name: "claim_id",
                table: "event_photos");
        }
    }
}
