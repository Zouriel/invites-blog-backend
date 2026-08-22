using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurableRsvpQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "answers_json",
                table: "rsvp_responses",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "rsvp_questions_json",
                table: "campaigns",
                type: "jsonb",
                nullable: false,
                defaultValue: "{\"questions\":[]}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "answers_json",
                table: "rsvp_responses");

            migrationBuilder.DropColumn(
                name: "rsvp_questions_json",
                table: "campaigns");
        }
    }
}
