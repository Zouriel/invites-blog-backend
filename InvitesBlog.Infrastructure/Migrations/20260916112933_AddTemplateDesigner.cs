using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateDesigner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "public_publishing_revoked_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "unlisted_by_admin_at",
                table: "templates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "template_designs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    scene_json = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_revision = table.Column<int>(type: "integer", nullable: true),
                    last_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_designs", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_designs_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "template_design_publishes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    design_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<int>(type: "integer", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_design_publishes", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_design_publishes_template_designs_design_id",
                        column: x => x.design_id,
                        principalTable: "template_designs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_template_design_publishes_design_id",
                table: "template_design_publishes",
                column: "design_id");

            migrationBuilder.CreateIndex(
                name: "idx_template_design_publishes_user_time",
                table: "template_design_publishes",
                columns: new[] { "user_id", "published_at" });

            migrationBuilder.CreateIndex(
                name: "idx_template_designs_owner_updated",
                table: "template_designs",
                columns: new[] { "owner_user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "idx_template_designs_template_id",
                table: "template_designs",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "idx_template_reports_queue",
                table: "template_reports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_template_reports_template_id",
                table: "template_reports",
                column: "template_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "template_design_publishes");

            migrationBuilder.DropTable(
                name: "template_reports");

            migrationBuilder.DropTable(
                name: "template_designs");

            migrationBuilder.DropColumn(
                name: "public_publishing_revoked_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "unlisted_by_admin_at",
                table: "templates");
        }
    }
}
