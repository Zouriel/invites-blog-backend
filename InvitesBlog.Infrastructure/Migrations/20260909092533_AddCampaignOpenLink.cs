using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignOpenLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "open_link_code",
                table: "campaigns",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_campaigns_open_link_code",
                table: "campaigns",
                column: "open_link_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_campaigns_open_link_code",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "open_link_code",
                table: "campaigns");
        }
    }
}
