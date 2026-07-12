using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Abstractions.Persistence;

// Entity-specific repositories: they extend the generic base with queries whose intent is clear
// from the method name (spec §Repositories). No business logic lives here — only data access.

public interface ITemplateRepository : IRepository<Template>
{
    Task<Template?> GetActiveBySlugAsync(string slug, CancellationToken ct = default);
    Task<Template?> GetActiveByIdAsync(Guid id, CancellationToken ct = default);
}

public interface ICampaignRepository : IRepository<Campaign>
{
    Task<Campaign?> GetByAccessTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<Campaign?> GetByDashboardTokenHashAsync(Guid id, string tokenHash, CancellationToken ct = default);
}

public interface IInviterRepository : IRepository<Inviter>
{
    Task<Inviter?> GetByEmailAsync(string email, CancellationToken ct = default);
}

public interface IGuestRepository : IRepository<Guest>
{
    Task<IReadOnlyList<Guest>> ListByCampaignAsync(Guid campaignId, bool includeOptedOut, CancellationToken ct = default);
    Task<int> CountByCampaignAsync(Guid campaignId, CancellationToken ct = default);
}

public interface IInviteRepository : IRepository<Invite>
{
    Task<Invite?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<Invite>> ListByCampaignAsync(Guid campaignId, CancellationToken ct = default);
}

public interface IPaymentRepository : IRepository<Payment>
{
    Task<Payment?> GetBySessionIdAsync(string providerSessionId, CancellationToken ct = default);
}

public interface IOtpChallengeRepository : IRepository<OtpChallenge>
{
    Task<int> CountRecentSendsAsync(string? phone, string? email, DateTimeOffset since, CancellationToken ct = default);
}

public interface ISuppressionRepository : IRepository<SuppressionEntry>
{
    Task<bool> ExistsByHashAsync(string contactHash, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListHashesAsync(CancellationToken ct = default);
}
