using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFaceGrouping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "face_grouping_confirmed_at",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "face_grouping_confirmed_by",
                table: "media_buckets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "face_grouping_on",
                table: "media_buckets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "face_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_face_jobs_event_photos_id",
                        column: x => x.id,
                        principalTable: "event_photos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_face_jobs_media_buckets_bucket_id",
                        column: x => x.bucket_id,
                        principalTable: "media_buckets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "face_people",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ignored = table.Column<bool>(type: "boolean", nullable: false),
                    centroid = table.Column<float[]>(type: "real[]", nullable: false),
                    face_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_people", x => x.id);
                    table.ForeignKey(
                        name: "FK_face_people_media_buckets_bucket_id",
                        column: x => x.bucket_id,
                        principalTable: "media_buckets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "photo_faces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    photo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<float>(type: "real", nullable: false),
                    y = table.Column<float>(type: "real", nullable: false),
                    w = table.Column<float>(type: "real", nullable: false),
                    h = table.Column<float>(type: "real", nullable: false),
                    score = table.Column<float>(type: "real", nullable: false),
                    embedding = table.Column<float[]>(type: "real[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_photo_faces", x => x.id);
                    table.ForeignKey(
                        name: "FK_photo_faces_event_photos_photo_id",
                        column: x => x.photo_id,
                        principalTable: "event_photos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_photo_faces_face_people_person_id",
                        column: x => x.person_id,
                        principalTable: "face_people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_photo_faces_media_buckets_bucket_id",
                        column: x => x.bucket_id,
                        principalTable: "media_buckets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_face_jobs_bucket",
                table: "face_jobs",
                column: "bucket_id");

            migrationBuilder.CreateIndex(
                name: "idx_face_jobs_due",
                table: "face_jobs",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "idx_face_people_bucket",
                table: "face_people",
                column: "bucket_id");

            migrationBuilder.CreateIndex(
                name: "idx_photo_faces_bucket_person",
                table: "photo_faces",
                columns: new[] { "bucket_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "idx_photo_faces_photo",
                table: "photo_faces",
                column: "photo_id");

            migrationBuilder.CreateIndex(
                name: "IX_photo_faces_person_id",
                table: "photo_faces",
                column: "person_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "face_jobs");

            migrationBuilder.DropTable(
                name: "photo_faces");

            migrationBuilder.DropTable(
                name: "face_people");

            migrationBuilder.DropColumn(
                name: "face_grouping_confirmed_at",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "face_grouping_confirmed_by",
                table: "media_buckets");

            migrationBuilder.DropColumn(
                name: "face_grouping_on",
                table: "media_buckets");
        }
    }
}
