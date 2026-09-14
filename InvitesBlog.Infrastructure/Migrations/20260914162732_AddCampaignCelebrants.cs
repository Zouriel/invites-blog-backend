using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCelebrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaign_celebrants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    phone_e164 = table.Column<string>(type: "text", nullable: true),
                    can_manage = table.Column<bool>(type: "boolean", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_celebrants", x => x.id);
                    table.ForeignKey(
                        name: "FK_campaign_celebrants_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_campaign_celebrants_campaign_id",
                table: "campaign_celebrants",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "idx_campaign_celebrants_email",
                table: "campaign_celebrants",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "idx_campaign_celebrants_phone_e164",
                table: "campaign_celebrants",
                column: "phone_e164");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_celebrants");
        }
    }
}
