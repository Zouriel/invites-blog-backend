using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestedDesignerOnInquiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "requested_designer_user_id",
                table: "inquiries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_inquiries_requested_designer",
                table: "inquiries",
                column: "requested_designer_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_inquiries_requested_designer",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "requested_designer_user_id",
                table: "inquiries");
        }
    }
}
