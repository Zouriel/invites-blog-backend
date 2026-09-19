using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <summary>
    /// Events made before plans v2 were promised free sending (it was never charged). Once the
    /// allowance was enforced, a Free event that had already emailed some guests could not email the
    /// next one. Every upcoming, not-cancelled event made before the plans went live gets one block of
    /// 100 extra emails, the same as an admin pressing "+100 emails" on it.
    /// </summary>
    public partial class GrandfatherSendingForEarlierEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8 = CampaignStatus.Cancelled. 10:15 UTC on 2026-09-19 is when plans v2 went live.
            migrationBuilder.Sql(@"
                UPDATE campaigns
                   SET paid_invite_capacity = paid_invite_capacity + 100
                 WHERE created_at < TIMESTAMPTZ '2026-09-19 10:15:00+00'
                   AND event_start_at > now()
                   AND status <> 8;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: which events got the block depends on when this ran, and taking capacity
            // back from an event mid-send is worse than leaving it.
        }
    }
}
