using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Delivery;

/// <summary>
/// Takes one email from an event's allowance before it goes, in the order the owner set (2026-11-14): the event's
/// pass, then the organiser's monthly allowance, then emails bought for the event. See
/// <see cref="SendingAllowanceDto"/>.
///
/// <para>Race-safe: the charge is decided and written inside one transaction holding a Postgres advisory lock for
/// the organiser (or the event, when it has none), and the invite is marked before the email is sent, so a second
/// send at the same moment sees the first one's charge. If the email then fails, <see cref="RefundAsync"/> gives
/// it back, so only emails actually sent are counted. An API that dies mid-send leaves its charge in place, which
/// errs towards counting one too many, never one too few.</para>
/// </summary>
public sealed class EmailAllowanceGate(AppDbContext db, IPlanService plans, TimeProvider clock) : IEmailAllowanceGate
{
    /// <summary>First half of the advisory lock key: "email allowance", so it can't meet another lock.</summary>
    private const int LockSpace = 0x1B_E3A1;

    public async Task<EmailCharge?> ChargeAsync(Campaign campaign, Invite invite, CancellationToken ct = default)
    {
        var previous = (invite.FirstEmailedAt, invite.EmailPaidBy);

        // A guest the event (pass or bought emails) already paid for is sent again for free, as always.
        if (invite.FirstEmailedAt is not null && invite.EmailPaidBy == EmailPaidBy.Event)
            return new EmailCharge(null, null, previous.FirstEmailedAt, previous.EmailPaidBy);

        var postgres = db.Database.IsNpgsql();
        await using var tx = postgres && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;

        var plan = await plans.ForCampaignAsync(campaign.Id, ct);
        var owner = plan.OwnerUserId;
        if (postgres)
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, {KeyOf(owner ?? campaign.Id)})", ct);

        var now = clock.GetUtcNow();
        // Read after the lock, so a charge another send just committed is counted.
        var eventUsed = await db.Invites.AsNoTracking().CountAsync(i => i.CampaignId == campaign.Id && i.Id != invite.Id
            && i.FirstEmailedAt != null && i.EmailPaidBy == EmailPaidBy.Event, ct);
        var extra = await db.Campaigns.AsNoTracking().Where(c => c.Id == campaign.Id)
            .Select(c => c.PaidInviteCapacity).FirstOrDefaultAsync(ct);

        EmailPaidBy? paidBy = null;
        MonthlyEmailSend? ledger = null;
        if (eventUsed < plan.IncludedInvites)
            paidBy = EmailPaidBy.Event;                       // 1. the pass
        else if (owner is { } account && await MonthlyLeftAsync(account, now, ct) > 0)
        {
            paidBy = EmailPaidBy.Monthly;                     // 2. the organiser's month
            ledger = new MonthlyEmailSend
            {
                Id = Guid.NewGuid(), AccountId = account, Month = EmailMonth.Of(now),
                CampaignId = campaign.Id, InviteId = invite.Id, CreatedAt = now,
            };
            db.MonthlyEmailSends.Add(ledger);
        }
        else if (eventUsed < plan.IncludedInvites + Math.Max(0, extra))
            paidBy = EmailPaidBy.Event;                       // 3. emails bought for the event

        if (paidBy is null)
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            return null;
        }

        if (db.Entry(invite).State == EntityState.Detached) db.Invites.Add(invite);
        invite.FirstEmailedAt ??= now;
        invite.EmailPaidBy = paidBy.Value;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new EmailCharge(paidBy, ledger?.Id, previous.FirstEmailedAt, previous.EmailPaidBy);
    }

    public async Task RefundAsync(EmailCharge charge, Invite invite, CancellationToken ct = default)
    {
        if (charge.PaidBy is null) return;   // it cost nothing
        if (charge.LedgerId is { } id)
        {
            var row = await db.MonthlyEmailSends.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (row is not null) db.MonthlyEmailSends.Remove(row);
        }
        invite.FirstEmailedAt = charge.PreviousFirstEmailedAt;
        invite.EmailPaidBy = charge.PreviousPaidBy;
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> MonthlyLeftAsync(Guid account, DateTimeOffset now, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == account)
            .Select(u => new { u.SubscriptionTier, u.SubscriptionEndsAt }).FirstOrDefaultAsync(ct);
        // A lapsed subscription has no allowance.
        if (user is null || !PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now)) return 0;
        var month = EmailMonth.Of(now);
        var spent = await db.MonthlyEmailSends.CountAsync(m => m.AccountId == account && m.Month == month, ct);
        return PlanCatalog.MonthlyEmails(user.SubscriptionTier) - spent;
    }

    /// <summary>A stable 32-bit key from a Guid (never GetHashCode, which changes per process).</summary>
    private static int KeyOf(Guid id)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b);
        return BitConverter.ToInt32(b[..4]) ^ BitConverter.ToInt32(b[4..8]) ^ BitConverter.ToInt32(b[8..12]) ^ BitConverter.ToInt32(b[12..]);
    }
}
