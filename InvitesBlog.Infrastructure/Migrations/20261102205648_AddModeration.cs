using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "uploader_user_id",
                table: "pending_uploads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "uploader_user_id",
                table: "event_photos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "post_hidden_at",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "content_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    snapshot = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_content_reports_users_reporter_user_id",
                        column: x => x.reporter_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocker_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_blocks", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_blocks_users_blocked_user_id",
                        column: x => x.blocked_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_blocks_users_blocker_user_id",
                        column: x => x.blocker_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_content_reports_queue",
                table: "content_reports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_content_reports_reporter_kind",
                table: "content_reports",
                columns: new[] { "reporter_user_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "idx_content_reports_target",
                table: "content_reports",
                columns: new[] { "kind", "target_id" });

            migrationBuilder.CreateIndex(
                name: "idx_user_blocks_blocked",
                table: "user_blocks",
                column: "blocked_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_blocks_pair",
                table: "user_blocks",
                columns: new[] { "blocker_user_id", "blocked_user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_reports");

            migrationBuilder.DropTable(
                name: "user_blocks");

            migrationBuilder.DropColumn(
                name: "uploader_user_id",
                table: "pending_uploads");

            migrationBuilder.DropColumn(
                name: "uploader_user_id",
                table: "event_photos");

            migrationBuilder.DropColumn(
                name: "post_hidden_at",
                table: "campaigns");
        }
    }
}
