using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    uploader_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    qr_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incoming_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    declared_bytes = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_uploads", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_pending_uploads_status",
                table: "pending_uploads",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_uploads");
        }
    }
}
