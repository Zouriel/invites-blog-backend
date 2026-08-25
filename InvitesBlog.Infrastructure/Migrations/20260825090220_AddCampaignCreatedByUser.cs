using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCreatedByUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                table: "campaigns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_campaign_created_by",
                table: "campaigns",
                column: "created_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_campaign_created_by",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "campaigns");
        }
    }
}
