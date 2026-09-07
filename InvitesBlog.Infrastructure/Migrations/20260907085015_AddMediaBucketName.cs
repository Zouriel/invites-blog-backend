using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaBucketName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "media_buckets",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                // Not the empty string EF scaffolds from the CLR default: every bucket that already
                // exists predates names, and an unnamed row would render as a blank line where its
                // title used to be. They are all what they always were — the night's bucket.
                defaultValue: "Night's bucket");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "name",
                table: "media_buckets");
        }
    }
}
