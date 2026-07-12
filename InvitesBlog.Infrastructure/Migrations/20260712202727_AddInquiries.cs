using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInquiries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inquiries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    occasion = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    colors = table.Column<string>(type: "text", nullable: true),
                    references = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    has_attended = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    attended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    template_issued = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    template_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    issued_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inquiries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_inquiries_email",
                table: "inquiries",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "idx_inquiries_queue",
                table: "inquiries",
                columns: new[] { "has_attended", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inquiries");
        }
    }
}
