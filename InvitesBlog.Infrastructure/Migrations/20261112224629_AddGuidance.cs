using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "guidance_seen",
                table: "users",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "guidance_tips_off",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "acknowledged_at",
                table: "campaign_celebrants",
                type: "timestamp with time zone",
                nullable: true);

            // Everyone already on an event has known about it for a while: no "you've been added" card
            // for them. Only celebrants added from now on start unacknowledged (docs/GUIDANCE.md).
            migrationBuilder.Sql("UPDATE campaign_celebrants SET acknowledged_at = now() WHERE acknowledged_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "guidance_seen",
                table: "users");

            migrationBuilder.DropColumn(
                name: "guidance_tips_off",
                table: "users");

            migrationBuilder.DropColumn(
                name: "acknowledged_at",
                table: "campaign_celebrants");
        }
    }
}
