using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "roles",
                table: "guests",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            // Every guest written before the list existed keeps the one role they had.
            migrationBuilder.Sql("UPDATE guests SET roles = ARRAY[role] WHERE role IS NOT NULL AND btrim(role) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "roles",
                table: "guests");
        }
    }
}
