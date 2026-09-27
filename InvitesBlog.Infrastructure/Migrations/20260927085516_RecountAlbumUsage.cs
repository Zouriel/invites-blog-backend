using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Two corrections to album storage, data only.
    /// <list type="bullet">
    /// <item>Per-album capacities are cleared. An album's space is its event plan's; the old per-album
    /// figure (2 GiB on albums made before 2026-09-15) made one Free event show and allow 2 GB.</item>
    /// <item>Usage is recounted from live photos. A deleted photo's bytes used to stay counted forever;
    /// deleting one now gives its space back, and this gives back what earlier deletes kept.</item>
    /// </list>
    /// Down restores neither: the old capacities were 2147483648 on albums made before 2026-09-15, and
    /// the old usage included deleted photos.
    /// </summary>
    public partial class RecountAlbumUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE media_buckets SET capacity_bytes = 0 WHERE capacity_bytes <> 0;");
            migrationBuilder.Sql("""
                UPDATE media_buckets b
                SET used_bytes = COALESCE((
                    SELECT SUM(p.size_bytes) FROM event_photos p
                    WHERE p.bucket_id = b.id AND p.deleted_at IS NULL), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
