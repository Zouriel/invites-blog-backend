using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioCoverAndHostUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cover_image_url",
                table: "portfolios",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            // Old host uploads in Studio-organised events get the organiser as uploader (see PortfolioBackfill).
            migrationBuilder.Sql(InvitesBlog.Infrastructure.Portfolios.PortfolioBackfill.HostUploadsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cover_image_url",
                table: "portfolios");
        }
    }
}
