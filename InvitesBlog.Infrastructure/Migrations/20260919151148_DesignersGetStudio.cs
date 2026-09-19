using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <summary>
    /// The template designer now comes with the Studio plan (DesignerAccessService). Everyone who was
    /// already a designer without a plan keeps designing: they get Studio, with no end date.
    /// </summary>
    public partial class DesignersGetStudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2 = SubscriptionTier.Studio, 0 = None.
            migrationBuilder.Sql(@"
                UPDATE users
                   SET subscription_tier = 2, subscription_ends_at = NULL
                 WHERE subscription_tier = 0
                   AND id IN (SELECT ur.user_id FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE r.name = 'Designer');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: who was given Studio this way isn't recorded apart from the plan itself.
        }
    }
}
