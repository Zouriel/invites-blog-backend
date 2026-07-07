using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAndTemplateVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "assigned_email",
                table: "templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "visibility",
                table: "templates",
                type: "text",
                nullable: false,
                defaultValue: "Public");

            migrationBuilder.AddColumn<string>(
                name: "roles_json",
                table: "campaigns",
                type: "jsonb",
                nullable: false,
                defaultValue: "{\"roles\":[]}");

            migrationBuilder.CreateIndex(
                name: "idx_templates_assigned_email",
                table: "templates",
                column: "assigned_email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_templates_assigned_email",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "assigned_email",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "roles_json",
                table: "campaigns");
        }
    }
}
