using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionsAndDesignerFee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "idx_inquiries_assigned_designer",
                table: "inquiries",
                column: "assigned_designer_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_inquiries_assigned_designer",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "assigned_designer_user_id",
                table: "inquiries");

            migrationBuilder.DropColumn(
                name: "commission_price",
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
    }
}
