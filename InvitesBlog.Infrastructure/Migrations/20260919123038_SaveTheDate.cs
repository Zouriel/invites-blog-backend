using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SaveTheDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "all_day",
                table: "campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "invitation_campaign_id",
                table: "campaigns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "all_day",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "invitation_campaign_id",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "campaigns");
        }
    }
}
