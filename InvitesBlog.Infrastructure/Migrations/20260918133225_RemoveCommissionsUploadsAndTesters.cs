using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCommissionsUploadsAndTesters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_templates");

            migrationBuilder.DropTable(
                name: "feature_releases");

            migrationBuilder.DropTable(
                name: "feature_testers");

            migrationBuilder.DropIndex(
                name: "idx_inquiries_assigned_designer",
                table: "inquiries");

            migrationBuilder.DropIndex(
                name: "idx_inquiries_requested_designer",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "commission_price",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "designer_consent_to_publish",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "requested_by_email",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "requested_by_user_id",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "requester_consent_to_publish",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "usage_price",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "assigned_designer_user_id",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "commission_price",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "issued_template_id",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "requested_designer_user_id",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "template_issued",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "template_issued_at",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "usage_price",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "designer_fee",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "designer_fee_name",
                table: "campaigns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "commission_price",
                table: "templates",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "designer_consent_to_publish",
                table: "templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "requested_by_email",
                table: "templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requested_by_user_id",
                table: "templates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requester_consent_to_publish",
                table: "templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "usage_price",
                table: "templates",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_designer_user_id",
                table: "inquiries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "commission_price",
                table: "inquiries",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "issued_template_id",
                table: "inquiries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requested_designer_user_id",
                table: "inquiries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "template_issued",
                table: "inquiries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "template_issued_at",
                table: "inquiries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "usage_price",
                table: "inquiries",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "designer_fee",
                table: "campaigns",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "designer_fee_name",
                table: "campaigns",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "custom_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    commission_price = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    designer_consent_to_publish = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    designer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    html = table.Column<string>(type: "text", nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    name = table.Column<string>(type: "text", nullable: false),
                    package_url = table.Column<string>(type: "text", nullable: true),
                    preview_image_url = table.Column<string>(type: "text", nullable: true),
                    published_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    requested_by_email = table.Column<string>(type: "text", nullable: true),
                    requester_consent_to_publish = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    usage_price = table.Column<decimal>(type: "numeric(12,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feature_releases",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_releases", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "feature_testers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    features = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_testers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_inquiries_assigned_designer",
                table: "inquiries",
                column: "assigned_designer_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_inquiries_requested_designer",
                table: "inquiries",
                column: "requested_designer_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_custom_templates_designer_user_id",
                table: "custom_templates",
                column: "designer_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_custom_templates_queue",
                table: "custom_templates",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_custom_templates_slug",
                table: "custom_templates",
                column: "slug");

            migrationBuilder.CreateIndex(
                name: "idx_feature_testers_email",
                table: "feature_testers",
                column: "email",
                unique: true);
        }
    }
}
