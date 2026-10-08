using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordTermsAndRenewals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auto_renew_kind",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_customer_id",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "renewal_failures",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "renewal_last_tried_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_renew",
                table: "payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "terms_accepted_at",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "terms_version",
                table: "payments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_renew_kind",
                table: "users");

            migrationBuilder.DropColumn(
                name: "payment_customer_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "renewal_failures",
                table: "users");

            migrationBuilder.DropColumn(
                name: "renewal_last_tried_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "auto_renew",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "terms_accepted_at",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "terms_version",
                table: "payments");
        }
    }
}
