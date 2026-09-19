using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PassRemindersAndLiveEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pass_notice_stage",
                table: "campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Events are drafts until they're finished now (no album, codes or camera before then).
            // Photos-only events made under the old flow were never "finished" — they were simply
            // live — so they stay live: 0 = Draft, 6 = Dispatched, kind 0 = invitation.
            migrationBuilder.Sql(@"
                UPDATE campaigns
                   SET status = 6
                 WHERE status = 0
                   AND kind = 0
                   AND coalesce(template_package_url, '') = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pass_notice_stage",
                table: "campaigns");
        }
    }
}
