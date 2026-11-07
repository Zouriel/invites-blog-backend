using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_micros = table.Column<long>(type: "bigint", nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_usage", x => x.id);
                    table.ForeignKey(
                        name: "FK_assistant_usage_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "design_assistant_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    design_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_json = table.Column<string>(type: "text", nullable: false),
                    starts_turn = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_design_assistant_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_design_assistant_messages_template_designs_design_id",
                        column: x => x.design_id,
                        principalTable: "template_designs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_design_assistant_messages_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_assistant_usage_user_month",
                table: "assistant_usage",
                columns: new[] { "user_id", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_design_assistant_messages_design_seq",
                table: "design_assistant_messages",
                columns: new[] { "design_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_design_assistant_messages_user_id",
                table: "design_assistant_messages",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_usage");

            migrationBuilder.DropTable(
                name: "design_assistant_messages");
        }
    }
}
