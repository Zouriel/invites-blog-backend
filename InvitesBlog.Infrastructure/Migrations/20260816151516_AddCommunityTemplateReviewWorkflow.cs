using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunityTemplateReviewWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // custom_templates was created by InitialSchema for the never-wired declarative-scene design
            // (no service ever wrote a row). Its scene columns are dropped rather than renamed: EF guessed
            // compiler_version -> slug / inviter_id -> designer_user_id, which would have carried a
            // compiler version into a slug column had the table not been empty.
            migrationBuilder.DropColumn(
                name: "anonymous_attribution",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "scene_json",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "compiler_version",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "inviter_id",
                table: "custom_templates");

            migrationBuilder.AddColumn<Guid>(
                name: "designer_user_id",
                table: "custom_templates",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<string>(
                name: "slug",
                table: "custom_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

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

            migrationBuilder.AddColumn<Guid>(
                name: "designer_user_id",
                table: "templates",
                type: "uuid",
                nullable: true);

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

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "templates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            // Existing templates have never been edited — their last update IS their creation.
            migrationBuilder.Sql("UPDATE templates SET updated_at = created_at;");

            migrationBuilder.AddColumn<decimal>(
                name: "usage_price",
                table: "templates",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "category",
                table: "custom_templates",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "commission_price",
                table: "custom_templates",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "custom_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "designer_consent_to_publish",
                table: "custom_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "html",
                table: "custom_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "manifest_json",
                table: "custom_templates",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "package_url",
                table: "custom_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preview_image_url",
                table: "custom_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "custom_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_by_email",
                table: "custom_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requester_consent_to_publish",
                table: "custom_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "usage_price",
                table: "custom_templates",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_manifest_json",
                table: "campaigns",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            // Existing campaigns read structure off the live template row. Freeze what they're using NOW,
            // so the snapshot is already correct for them the first time a re-review changes a template.
            migrationBuilder.Sql(
                "UPDATE campaigns c SET template_manifest_json = t.manifest_json " +
                "FROM templates t WHERE t.id = c.template_id;");

            migrationBuilder.CreateIndex(
                name: "idx_templates_designer_user_id",
                table: "templates",
                column: "designer_user_id");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_templates_designer_user_id",
                table: "templates");

            migrationBuilder.DropIndex(
                name: "idx_custom_templates_designer_user_id",
                table: "custom_templates");

            migrationBuilder.DropIndex(
                name: "idx_custom_templates_queue",
                table: "custom_templates");

            migrationBuilder.DropIndex(
                name: "idx_custom_templates_slug",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "commission_price",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "designer_consent_to_publish",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "designer_user_id",
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
                name: "updated_at",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "usage_price",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "commission_price",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "description",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "designer_consent_to_publish",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "html",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "manifest_json",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "package_url",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "preview_image_url",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "requested_by_email",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "requester_consent_to_publish",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "usage_price",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "template_manifest_json",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "slug",
                table: "custom_templates");

            migrationBuilder.DropColumn(
                name: "designer_user_id",
                table: "custom_templates");

            migrationBuilder.AddColumn<Guid>(
                name: "inviter_id",
                table: "custom_templates",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<string>(
                name: "compiler_version",
                table: "custom_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "category",
                table: "custom_templates",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "anonymous_attribution",
                table: "custom_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "scene_json",
                table: "custom_templates",
                type: "jsonb",
                nullable: false,
                defaultValue: "");
        }
    }
}
