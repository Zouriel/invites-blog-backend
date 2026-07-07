namespace InvitesBlog.Application.Common;

/// <summary>
/// Common list query parameters — pagination, search, sorting (spec §API Structure — keep
/// pagination/filtering/sorting/searching consistent across entities). Entity-specific filter
/// classes extend this in the filters layer.
/// </summary>
public class PaginationRequest
{
    private const int MaxPageSize = 100;
    private int _pageSize = 20;

    public int Page { get; set; } = 1;

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value is < 1 or > MaxPageSize ? 20 : value;
    }

    public string? Search { get; set; }
    public string? SortBy { get; set; }
    public string SortDir { get; set; } = "asc";

    public bool Descending => SortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);
    public int Skip => (Math.Max(1, Page) - 1) * PageSize;
}

/// <summary>A page of results plus the metadata clients need to paginate.</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public static PagedResult<T> Create(IReadOnlyList<T> items, int totalCount, PaginationRequest request) =>
        new() { Items = items, TotalCount = totalCount, Page = request.Page, PageSize = request.PageSize };
}
