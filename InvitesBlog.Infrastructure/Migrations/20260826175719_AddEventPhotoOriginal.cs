using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventPhotoOriginal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "original_url",
                table: "event_photos",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Photos taken before the box kept originals have none — the 2048px viewing copy is the
            // largest version of them that exists. Point at that rather than leaving an empty string
            // that would render as a broken download link.
            migrationBuilder.Sql("UPDATE event_photos SET original_url = url WHERE original_url = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "original_url",
                table: "event_photos");
        }
    }
}
