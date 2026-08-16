using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PinCampaignTemplatePackageUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "template_package_url",
                table: "campaigns",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill each campaign with the package of the version IT pinned, which is not always
            // the template's current one: an approved edit moves the live row forward. Package URLs
            // end in "…/{slug}@{version}/", so the version segment is rewritten to the pinned one
            // rather than copying the live URL wholesale.
            migrationBuilder.Sql(@"
UPDATE campaigns c
SET template_package_url =
    CASE WHEN t.version = c.template_version
         THEN t.package_url
         ELSE regexp_replace(t.package_url, '@[^/@]+/?$', '@' || c.template_version || '/')
    END
FROM templates t
WHERE t.id = c.template_id
  AND c.template_package_url = ''
  AND t.package_url <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "template_package_url",
                table: "campaigns");
        }
    }
}
