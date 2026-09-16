using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Features;

public sealed record FeatureDto(string Key, string Name, string Description, bool Released, DateTimeOffset? ReleasedAt, int Testers);

public sealed record FeatureTesterDto(
    Guid Id, string Email, IReadOnlyList<string> Features, string? Note, bool HasAccount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record SaveTesterRequest(string Email, IReadOnlyList<string> Features, string? Note);

public interface IFeatureAccessService
{
    /// <summary>Whether the signed-in caller may use a feature: released, an admin, or a tester holding it.</summary>
    Task<bool> HasAsync(string feature, CancellationToken ct = default);

    /// <summary>Every feature the caller has, for the app to decide what to show.</summary>
    Task<IReadOnlyList<string>> MineAsync(CancellationToken ct = default);
}

public interface ITesterAdminService
{
    Task<IReadOnlyList<FeatureDto>> FeaturesAsync(CancellationToken ct = default);
    Task<FeatureDto> SetReleasedAsync(string key, bool released, CancellationToken ct = default);
    Task<IReadOnlyList<FeatureTesterDto>> ListAsync(CancellationToken ct = default);
    Task<FeatureTesterDto> AddAsync(SaveTesterRequest request, CancellationToken ct = default);
    Task<FeatureTesterDto> UpdateAsync(Guid id, SaveTesterRequest request, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Features that aren't released yet reach only the people an admin lists as testers. Checked on the
/// server for every request to the feature — hiding the entry points in the app is a courtesy, not
/// the gate.
///
/// <para>Admins always have access: they're the ones deciding whether a feature is ready.</para>
/// </summary>
public sealed class FeatureAccessService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<FeatureTester> testers,
    IRepository<FeatureRelease> releases) : IFeatureAccessService
{
    public async Task<bool> HasAsync(string feature, CancellationToken ct = default) =>
        (await MineAsync(ct)).Contains(feature);

    public async Task<IReadOnlyList<string>> MineAsync(CancellationToken ct = default)
    {
        var released = await releases.Query().Select(r => r.Key).ToListAsync(ct);
        var mine = new HashSet<string>(released, StringComparer.Ordinal);
        if (currentUser.UserId is not { } userId) return mine.ToList();

        if (currentUser.HasPermission(Permissions.Admin.Access))
            return Domain.Entities.Features.All.Select(f => f.Key).ToList();

        var email = await users.Query().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(email)) return mine.ToList();
        var normalized = email.Trim().ToLowerInvariant();
        var tester = await testers.Query().FirstOrDefaultAsync(t => t.Email == normalized, ct);
        if (tester is not null) mine.UnionWith(tester.Features);
        return mine.Where(Domain.Entities.Features.IsKnown).ToList();
    }
}

/// <inheritdoc cref="ITesterAdminService"/>
public sealed class TesterAdminService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<FeatureTester> testers,
    IRepository<FeatureRelease> releases,
    IUnitOfWork uow) : ITesterAdminService
{
    public async Task<IReadOnlyList<FeatureDto>> FeaturesAsync(CancellationToken ct = default)
    {
        var released = await releases.Query().ToDictionaryAsync(r => r.Key, ct);
        var all = await testers.Query().Select(t => t.Features).ToListAsync(ct);
        return Domain.Entities.Features.All.Select(f => new FeatureDto(
            f.Key, f.Name, f.Description, released.ContainsKey(f.Key), released.GetValueOrDefault(f.Key)?.ReleasedAt,
            all.Count(list => list.Contains(f.Key)))).ToList();
    }

    public async Task<FeatureDto> SetReleasedAsync(string key, bool released, CancellationToken ct = default)
    {
        if (!Domain.Entities.Features.IsKnown(key)) throw new NotFoundException("That feature doesn't exist.", "feature_unknown");
        var row = await releases.Query(tracking: true).FirstOrDefaultAsync(r => r.Key == key, ct);
        if (released && row is null)
            await releases.AddAsync(new FeatureRelease { Key = key, ReleasedAt = DateTimeOffset.UtcNow, ReleasedByUserId = currentUser.UserId }, ct);
        else if (!released && row is not null)
            releases.Remove(row);
        await uow.SaveChangesAsync(ct);
        return (await FeaturesAsync(ct)).First(f => f.Key == key);
    }

    public async Task<IReadOnlyList<FeatureTesterDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await testers.Query().OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        var emails = rows.Select(r => r.Email).ToList();
        var withAccounts = await users.Query().Where(u => u.Email != null && emails.Contains(u.Email)).Select(u => u.Email!).ToListAsync(ct);
        var known = withAccounts.ToHashSet(StringComparer.Ordinal);
        return rows.Select(r => ToDto(r, known.Contains(r.Email))).ToList();
    }

    public async Task<FeatureTesterDto> AddAsync(SaveTesterRequest request, CancellationToken ct = default)
    {
        var email = NormalizeEmail(request.Email);
        var features = CleanFeatures(request.Features);
        var existing = await testers.Query(tracking: true).FirstOrDefaultAsync(t => t.Email == email, ct);
        if (existing is not null)
        {
            // Adding someone who's already listed adds the features rather than failing.
            existing.Features = existing.Features.Union(features).ToList();
            if (!string.IsNullOrWhiteSpace(request.Note)) existing.Note = CleanNote(request.Note);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await uow.SaveChangesAsync(ct);
            return ToDto(existing, await HasAccountAsync(email, ct));
        }

        var now = DateTimeOffset.UtcNow;
        var tester = new FeatureTester
        {
            Id = Guid.NewGuid(), Email = email, Features = features, Note = CleanNote(request.Note),
            AddedByUserId = currentUser.UserId, CreatedAt = now, UpdatedAt = now,
        };
        await testers.AddAsync(tester, ct);
        await uow.SaveChangesAsync(ct);
        return ToDto(tester, await HasAccountAsync(email, ct));
    }

    public async Task<FeatureTesterDto> UpdateAsync(Guid id, SaveTesterRequest request, CancellationToken ct = default)
    {
        var tester = await testers.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == id, ct)
                     ?? throw new NotFoundException("That tester isn't on the list.", "tester_not_found");
        var email = NormalizeEmail(request.Email);
        if (email != tester.Email && await testers.AnyAsync(t => t.Email == email, ct))
            throw new AlreadyExistsException("That email is already a tester.", "tester_exists");
        tester.Email = email;
        tester.Features = CleanFeatures(request.Features);
        tester.Note = CleanNote(request.Note);
        tester.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return ToDto(tester, await HasAccountAsync(email, ct));
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var tester = await testers.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == id, ct)
                     ?? throw new NotFoundException("That tester isn't on the list.", "tester_not_found");
        testers.Remove(tester);
        await uow.SaveChangesAsync(ct);
    }

    private Task<bool> HasAccountAsync(string email, CancellationToken ct) => users.AnyAsync(u => u.Email == email, ct);

    private static FeatureTesterDto ToDto(FeatureTester t, bool hasAccount) =>
        new(t.Id, t.Email, t.Features, t.Note, hasAccount, t.CreatedAt, t.UpdatedAt);

    private static string NormalizeEmail(string? email)
    {
        var e = (email ?? string.Empty).Trim().ToLowerInvariant();
        var at = e.IndexOf('@');
        if (e.Length > 254 || at <= 0 || at == e.Length - 1 || e.Contains(' ') || !e[(at + 1)..].Contains('.'))
            throw new BusinessRuleException("Enter a valid email address.", "email_invalid");
        return e;
    }

    private static List<string> CleanFeatures(IReadOnlyList<string>? features)
    {
        var list = (features ?? []).Where(Domain.Entities.Features.IsKnown).Distinct().ToList();
        if (list.Count == 0) throw new BusinessRuleException("Turn on at least one feature for this tester.", "features_required");
        return list;
    }

    private static string? CleanNote(string? note)
    {
        var n = note?.Trim();
        return string.IsNullOrEmpty(n) ? null : n.Length > 300 ? n[..300] : n;
    }
}
