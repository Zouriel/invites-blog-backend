using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanAddOns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "add_ons",
                table: "users",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            // Pricing 2026-11-13 (docs/PRICING-2026-11.md): the AI designer became an add-on to Premium. Everyone on
            // Premium now had it, so they keep it until their current period ends (subscription_ends_at, which a store
            // subscription keeps at its expiry), not renewing; then Premium renews without it at the new price. A
            // Premium given with no end date keeps it with no end. Tier 2 is Premium.
            migrationBuilder.Sql("""
                UPDATE users
                SET add_ons = jsonb_build_array(jsonb_build_object(
                    'key', 'designer-ai', 'endsAt', to_jsonb(subscription_ends_at), 'renews', false, 'source', 'carried-over'))
                WHERE subscription_tier = 2 AND (subscription_ends_at IS NULL OR subscription_ends_at > now());
                """);

            // A price list saved by an admin overrides the defaults in code: move it to the new prices too
            // (Premium 77 a month, the AI designer 77 a month). Untouched when there is no saved list.
            migrationBuilder.Sql("""
                UPDATE app_settings
                SET value_json = value_json || '{"PremiumMonthly": 77, "DesignerAiMonthly": 77}'::jsonb, updated_at = now()
                WHERE key = 'prices';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "add_ons",
                table: "users");
        }
    }
}
