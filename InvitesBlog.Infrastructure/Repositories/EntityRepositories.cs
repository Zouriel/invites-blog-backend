using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Repositories;

public sealed class TemplateRepository(AppDbContext db) : BaseRepository<Template>(db), ITemplateRepository
{
    public Task<Template?> GetActiveBySlugAsync(string slug, CancellationToken ct = default) =>
        Set.AsNoTracking().Where(t => t.Slug == slug && t.IsActive)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync(ct);

    public Task<Template?> GetActiveByIdAsync(Guid id, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(t => t.Id == id && t.IsActive, ct);
}

public sealed class CampaignRepository(AppDbContext db) : BaseRepository<Campaign>(db), ICampaignRepository
{
    public Task<Campaign?> GetByAccessTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(c => c.AccessTokenHash == tokenHash, ct);

    public Task<Campaign?> GetByDashboardTokenHashAsync(Guid id, string tokenHash, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(c => c.Id == id && c.DashboardTokenHash == tokenHash, ct);
}

public sealed class InviterRepository(AppDbContext db) : BaseRepository<Inviter>(db), IInviterRepository
{
    public Task<Inviter?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(i => i.Email == email.Trim().ToLower(), ct);
}

public sealed class GuestRepository(AppDbContext db) : BaseRepository<Guest>(db), IGuestRepository
{
    public async Task<IReadOnlyList<Guest>> ListByCampaignAsync(Guid campaignId, bool includeOptedOut, CancellationToken ct = default)
    {
        var query = Set.AsNoTracking().Where(g => g.CampaignId == campaignId);
        if (!includeOptedOut) query = query.Where(g => !g.OptedOut);
        return await query.ToListAsync(ct);
    }

    public Task<int> CountByCampaignAsync(Guid campaignId, CancellationToken ct = default) =>
        Set.CountAsync(g => g.CampaignId == campaignId, ct);
}

public sealed class InviteRepository(AppDbContext db) : BaseRepository<Invite>(db), IInviteRepository
{
    public Task<Invite?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<Invite>> ListByCampaignAsync(Guid campaignId, CancellationToken ct = default) =>
        await Set.AsNoTracking().Where(i => i.CampaignId == campaignId).ToListAsync(ct);
}

public sealed class PaymentRepository(AppDbContext db) : BaseRepository<Payment>(db), IPaymentRepository
{
    public Task<Payment?> GetBySessionIdAsync(string providerSessionId, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(p => p.ProviderSessionId == providerSessionId, ct);
}

public sealed class OtpChallengeRepository(AppDbContext db) : BaseRepository<OtpChallenge>(db), IOtpChallengeRepository
{
    public Task<int> CountRecentSendsAsync(string? phone, string? email, DateTimeOffset since, CancellationToken ct = default) =>
        Set.CountAsync(c => c.CreatedAt >= since &&
            (phone != null ? c.PhoneE164 == phone : c.Email == email), ct);
}

public sealed class SuppressionRepository(AppDbContext db) : BaseRepository<SuppressionEntry>(db), ISuppressionRepository
{
    public Task<bool> ExistsByHashAsync(string contactHash, CancellationToken ct = default) =>
        Set.AnyAsync(s => s.ContactHash == contactHash, ct);

    public async Task<IReadOnlyList<string>> ListHashesAsync(CancellationToken ct = default) =>
        await Set.AsNoTracking().Select(s => s.ContactHash).ToListAsync(ct);
}
