using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaBucketEventDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "event_date",
                table: "media_buckets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Backfill, or every bucket that already exists is permanently shut: the column default
            // is year 1, and the window closes 24 hours after the date. A bucket attached to an event
            // takes that event's date, which is the answer it would have been given at creation.
            migrationBuilder.Sql(@"
                UPDATE media_buckets b
                SET event_date = c.event_start_at
                FROM campaigns c
                WHERE b.campaign_id = c.id;");

            // Anything left has no campaign to take a date from — buckets made in the window between
            // standalone buckets shipping and this column existing. Their own creation date is the
            // only honest guess, and it at least leaves them open on the day they were made rather
            // than closed forever.
            migrationBuilder.Sql(@"
                UPDATE media_buckets
                SET event_date = created_at
                WHERE campaign_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "event_date",
                table: "media_buckets");
        }
    }
}
