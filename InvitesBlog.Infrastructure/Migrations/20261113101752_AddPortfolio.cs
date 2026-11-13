using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "reporter_user_id",
                table: "content_reports",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "portfolio_consents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consented_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_consents", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolio_consents_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_portfolio_consents_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portfolios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: ""),
                    tagline = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    logo_url = table.Column<string>(type: "text", nullable: true),
                    cover_photo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    theme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "oxblood"),
                    accent = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "theme"),
                    font = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "classic"),
                    whats_app = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    viber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    instagram = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    enquiries_on = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    setup_done = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    studio_ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolios", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolios_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portfolio_collections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    layout = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "grid"),
                    cover_photo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_collections", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolio_collections_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portfolio_enquiries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    contact = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    event_date = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_enquiries", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolio_enquiries_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portfolio_handle_redirects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_handle_redirects", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolio_handle_redirects_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portfolio_photos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_photo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    url = table.Column<string>(type: "text", nullable: false),
                    thumb_url = table.Column<string>(type: "text", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_photos", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolio_photos_portfolio_collections_collection_id",
                        column: x => x.collection_id,
                        principalTable: "portfolio_collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_portfolio_photos_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portfolio_sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    hidden = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_sections", x => x.id);
                    table.ForeignKey(
                        name: "FK_portfolio_sections_portfolio_collections_collection_id",
                        column: x => x.collection_id,
                        principalTable: "portfolio_collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_portfolio_sections_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_portfolio_collections_portfolio_id",
                table: "portfolio_collections",
                column: "portfolio_id");

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_consents_campaign_id",
                table: "portfolio_consents",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ux_portfolio_consents_user_campaign",
                table: "portfolio_consents",
                columns: new[] { "user_id", "campaign_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_portfolio_enquiries_portfolio_created",
                table: "portfolio_enquiries",
                columns: new[] { "portfolio_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_handle_redirects_portfolio_id",
                table: "portfolio_handle_redirects",
                column: "portfolio_id");

            migrationBuilder.CreateIndex(
                name: "ux_portfolio_handle_redirects_handle",
                table: "portfolio_handle_redirects",
                column: "handle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_portfolio_photos_campaign_id",
                table: "portfolio_photos",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "idx_portfolio_photos_order",
                table: "portfolio_photos",
                columns: new[] { "collection_id", "position" });

            migrationBuilder.CreateIndex(
                name: "idx_portfolio_photos_portfolio_id",
                table: "portfolio_photos",
                column: "portfolio_id");

            migrationBuilder.CreateIndex(
                name: "idx_portfolio_sections_order",
                table: "portfolio_sections",
                columns: new[] { "portfolio_id", "position" });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_sections_collection_id",
                table: "portfolio_sections",
                column: "collection_id");

            migrationBuilder.CreateIndex(
                name: "ux_portfolios_handle",
                table: "portfolios",
                column: "handle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_portfolios_user_id",
                table: "portfolios",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portfolio_consents");

            migrationBuilder.DropTable(
                name: "portfolio_enquiries");

            migrationBuilder.DropTable(
                name: "portfolio_handle_redirects");

            migrationBuilder.DropTable(
                name: "portfolio_photos");

            migrationBuilder.DropTable(
                name: "portfolio_sections");

            migrationBuilder.DropTable(
                name: "portfolio_collections");

            migrationBuilder.DropTable(
                name: "portfolios");

            migrationBuilder.AlterColumn<Guid>(
                name: "reporter_user_id",
                table: "content_reports",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
