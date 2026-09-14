using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "subscription_ends_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "subscription_tier",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "event_pass_until",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "media_deleted_at",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "media_notice_anchor",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "media_notice_stage",
                table: "campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);
                    // The Subscriber role becomes a Premium subscription, and the role and its three bucket
            // permissions go: what a subscription allows is now worked out from the plan.
            migrationBuilder.Sql("""
                UPDATE users SET subscription_tier = 2
                WHERE id IN (SELECT ur.user_id FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE r.name = 'Subscriber');
                DELETE FROM user_roles WHERE role_id IN (SELECT id FROM roles WHERE name = 'Subscriber');
                DELETE FROM role_permissions WHERE role_id IN (SELECT id FROM roles WHERE name = 'Subscriber');
                DELETE FROM roles WHERE name = 'Subscriber';
                DELETE FROM role_permissions WHERE permission_id IN
                    (SELECT id FROM permissions WHERE name IN ('buckets.multiple', 'buckets.extended_window', 'buckets.larger_sizes'));
                DELETE FROM permissions WHERE name IN ('buckets.multiple', 'buckets.extended_window', 'buckets.larger_sizes');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subscription_ends_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "subscription_tier",
                table: "users");

            migrationBuilder.DropColumn(
                name: "event_pass_until",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "media_deleted_at",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "media_notice_anchor",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "media_notice_stage",
                table: "campaigns");
        }
    }
}
