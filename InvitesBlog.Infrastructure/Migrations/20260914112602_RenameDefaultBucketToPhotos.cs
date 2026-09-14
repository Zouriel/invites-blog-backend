using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameDefaultBucketToPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default bucket name was "Night's bucket"; it is "Photos" now. Only names still at the
            // old default (or its numbered form) change. A name an owner chose stays as it is.
            migrationBuilder.Sql("UPDATE media_buckets SET name = 'Photos' WHERE name = 'Night''s bucket'");
            migrationBuilder.Sql("UPDATE media_buckets SET name = 'Photos' || substring(name from 15) WHERE name ~ '^Night''s bucket [0-9]+$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE media_buckets SET name = 'Night''s bucket' || substring(name from 7) WHERE name ~ '^Photos( [0-9]+)?$'");
        }
    }
}
