using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioLogoPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "logo_position",
                table: "portfolios",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "bottom-left");

            migrationBuilder.AddColumn<string>(
                name: "logo_size",
                table: "portfolios",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "medium");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "logo_position",
                table: "portfolios");

            migrationBuilder.DropColumn(
                name: "logo_size",
                table: "portfolios");
        }
    }
}
