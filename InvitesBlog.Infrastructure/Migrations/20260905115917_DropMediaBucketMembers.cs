using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropMediaBucketMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_bucket_members");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_bucket_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact = table.Column<string>(type: "text", nullable: false),
                    contact_type = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_bucket_members", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_media_bucket_member",
                table: "media_bucket_members",
                columns: new[] { "bucket_id", "contact" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_bucket_members_contact",
                table: "media_bucket_members",
                column: "contact");
        }
    }
}
