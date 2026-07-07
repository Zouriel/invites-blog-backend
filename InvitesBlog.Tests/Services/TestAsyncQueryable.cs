using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace InvitesBlog.Tests.Services;

// Canonical EF Core in-memory async query provider (per the EF Core testing docs). It lets a plain
// List<T>.AsQueryable() back a repository's Query() so services that call EF async operators
// (ToListAsync / CountAsync / FirstOrDefaultAsync / AnyAsync) execute against in-memory data.
// Deriving from EnumerableQuery<T> means nested sub-queries (e.g. Contains over another IQueryable)
// are handled, and Include/ThenInclude become safe no-ops on this non-EF provider.

internal static class AsyncQueryableExtensions
{
    /// <summary>Wraps an in-memory sequence as an async-capable IQueryable for repository Query() stubs.</summary>
    public static IQueryable<T> AsAsyncQueryable<T>(this IEnumerable<T> source) => new TestAsyncEnumerable<T>(source);
}

internal sealed class TestAsyncQueryProvider<TEntity> : IAsyncQueryProvider
{
    private readonly IQueryProvider _inner;

    internal TestAsyncQueryProvider(IQueryProvider inner) => _inner = inner;

    public IQueryable CreateQuery(Expression expression) => new TestAsyncEnumerable<TEntity>(expression);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new TestAsyncEnumerable<TElement>(expression);

    public object? Execute(Expression expression) => _inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => _inner.Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var expectedResultType = typeof(TResult).GetGenericArguments()[0];
        var executionResult = ((IQueryProvider)this).Execute(expression);
        return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(expectedResultType)
            .Invoke(null, new[] { executionResult })!;
    }
}

internal sealed class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public TestAsyncEnumerable(Expression expression) : base(expression) { }
    public TestAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable) { }

    IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<T>(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new TestAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
}

internal sealed class TestAsyncEnumerator<T>(IEnumerator<T> inner) : IAsyncEnumerator<T>
{
    public T Current => inner.Current;

    public ValueTask<bool> MoveNextAsync() => new(inner.MoveNext());

    public ValueTask DisposeAsync()
    {
        inner.Dispose();
        return ValueTask.CompletedTask;
    }
}
