using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Accounts;

/// <summary>
/// Finishes handing over an event whose owner deleted their account while the chosen celebrant had none.
/// Runs each time a sign-in token is issued, so a contact proved by signing up or by linking is what
/// claims it, and only the row marked for that contact can.
/// </summary>
public sealed class EventInheritance(AppDbContext db) : IEventInheritance
{
    public async Task ClaimAsync(AppUser user, CancellationToken ct = default)
    {
        var email = user.Email?.Trim().ToLowerInvariant();
        var phone = user.PhoneE164?.Trim();
        if (email is null && phone is null) return;

        var rows = await db.CampaignCelebrants
            .Where(c => c.InheritsOwnership && ((email != null && c.Email == email) || (phone != null && c.PhoneE164 == phone)))
            .Select(c => new { c.Id, c.CampaignId })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var now = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Campaigns.Where(c => c.Id == row.CampaignId && c.CreatedByUserId == null).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CreatedByUserId, user.Id).SetProperty(c => c.UpdatedAt, now), ct);
            await db.MediaBuckets.Where(b => b.CampaignId == row.CampaignId && b.OwnerUserId == Guid.Empty)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.OwnerUserId, user.Id), ct);
            await db.CampaignCelebrants.Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.InheritsOwnership, false), ct);
            await tx.CommitAsync(ct);
        }
    }
}
