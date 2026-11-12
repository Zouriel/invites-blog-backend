using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "push_muted",
                table: "users",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "faces_end_notice_for",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "faces_end_notice_stage",
                table: "media_buckets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "faces_opt_out_pushed_at",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "faces_opt_out_pushed_up_to",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "faces_ready_pushed_at",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "photos_pushed_at",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "photos_pushed_up_to",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reminder_pushed_at",
                table: "invites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ignored_at",
                table: "face_people",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ignored_by_user_id",
                table: "face_people",
                type: "uuid",
                nullable: true);

            // Photos already in albums were never news on a phone: the first "new photos" push covers only what
            // arrives after this. Albums already grouping faces aren't told their grouping has "finished".
            migrationBuilder.Sql("UPDATE media_buckets SET photos_pushed_up_to = now(), photos_pushed_at = now();");
            migrationBuilder.Sql("UPDATE media_buckets SET faces_ready_pushed_at = now() WHERE face_grouping_on;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "push_muted",
                table: "users");

            migrationBuilder.DropColumn(
                name: "faces_end_notice_for",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "faces_end_notice_stage",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "faces_opt_out_pushed_at",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "faces_opt_out_pushed_up_to",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "faces_ready_pushed_at",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "photos_pushed_at",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "photos_pushed_up_to",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "reminder_pushed_at",
                table: "invites");

            migrationBuilder.DropColumn(
                name: "ignored_at",
                table: "face_people");

            migrationBuilder.DropColumn(
                name: "ignored_by_user_id",
                table: "face_people");
        }
    }
}
