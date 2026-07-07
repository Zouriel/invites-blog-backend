using System.Linq.Expressions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Repositories;

/// <summary>
/// EF Core base repository shared by every entity repository (spec §Repositories). Holds only
/// generic data-access — never business logic. Reads are no-tracking by default.
/// </summary>
public class BaseRepository<T>(AppDbContext db) : IRepository<T> where T : class
{
    protected readonly AppDbContext Db = db;
    protected DbSet<T> Set => Db.Set<T>();

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await Set.FindAsync([id], ct);

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        Set.AsNoTracking().FirstOrDefaultAsync(predicate, ct);

    public async Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
    {
        var query = Set.AsNoTracking();
        if (predicate is not null) query = query.Where(predicate);
        return await query.ToListAsync(ct);
    }

    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        Set.AnyAsync(predicate, ct);

    public Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default) =>
        predicate is null ? Set.CountAsync(ct) : Set.CountAsync(predicate, ct);

    public IQueryable<T> Query(bool tracking = false) =>
        tracking ? Set : Set.AsNoTracking();

    public async Task AddAsync(T entity, CancellationToken ct = default) => await Set.AddAsync(entity, ct);
    public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default) => await Set.AddRangeAsync(entities, ct);
    public void Update(T entity) => Set.Update(entity);
    public void Remove(T entity) => Set.Remove(entity);
    public void RemoveRange(IEnumerable<T> entities) => Set.RemoveRange(entities);
}

/// <summary>EF Core unit of work — the one place SaveChanges is committed.</summary>
public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
