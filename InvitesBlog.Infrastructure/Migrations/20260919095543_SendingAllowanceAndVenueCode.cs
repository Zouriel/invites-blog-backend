using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SendingAllowanceAndVenueCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "code",
                table: "venues",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_emailed_at",
                table: "invites",
                type: "timestamp with time zone",
                nullable: true);

            // Guests who already received or opened their invitation count as emailed, so resending to
            // them never runs into an event's new emailed-invitation limit. (Status 2 = Sent, 4 = Viewed.)
            migrationBuilder.Sql("UPDATE invites SET first_emailed_at = created_at WHERE status IN (2, 4);");

            migrationBuilder.CreateIndex(
                name: "idx_venues_code",
                table: "venues",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_venues_code",
                table: "venues");

            migrationBuilder.DropColumn(
                name: "code",
                table: "venues");

            migrationBuilder.DropColumn(
                name: "first_emailed_at",
                table: "invites");
        }
    }
}
