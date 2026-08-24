using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedContactLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "verified_contact_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    phone_e164 = table.Column<string>(type: "text", nullable: false),
                    verified_from = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verified_contact_links", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_vcl_email",
                table: "verified_contact_links",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "idx_vcl_pair",
                table: "verified_contact_links",
                columns: new[] { "email", "phone_e164" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_vcl_phone",
                table: "verified_contact_links",
                column: "phone_e164");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verified_contact_links");
        }
    }
}
