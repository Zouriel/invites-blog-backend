using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Campaigns;

/// <summary>
/// Being on the guest list is what puts an event in somebody's inbox and feed. Sending them an
/// invitation is a separate step and may never happen (a phone-only guest has no channel yet), so
/// neither page may depend on an invite row. The one thing held back is an event the host hasn't
/// finished: its guest list is typed in before the event is ready for anyone to see.
/// </summary>
public static class GuestVisibility
{
    public static readonly List<CampaignStatus> Hidden =
        [CampaignStatus.Draft, CampaignStatus.PendingPayment, CampaignStatus.PaymentFailed];
}
