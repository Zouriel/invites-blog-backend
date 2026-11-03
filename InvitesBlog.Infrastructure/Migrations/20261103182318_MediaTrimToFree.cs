using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MediaTrimToFree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "keep_when_trimmed",
                table: "event_photos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "media_trim_at",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "media_trim_bytes",
                table: "campaigns",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "media_trimmed_at",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "keep_when_trimmed",
                table: "event_photos");

            migrationBuilder.DropColumn(
                name: "media_trim_at",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "media_trim_bytes",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "media_trimmed_at",
                table: "campaigns");
        }
    }
}
