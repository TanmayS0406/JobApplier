using Microsoft.AspNetCore.Routing;

namespace JobApplier.Models;

/// <summary>
/// How a list on the dashboard is paged, sorted and filtered. It comes from the query string, so every value is clamped to what the
/// page actually offers: a hand-edited address can never ask for a 10,000-row page or an unknown sort.
/// </summary>
public sealed record ListQuery(int Page, int PageSize, string Sort, int MinMatch)
{
    public const string SortNewest = "new";
    public const string SortMatchHigh = "match-desc";
    public const string SortMatchLow = "match-asc";
    public const string SortCompany = "company";

    public static readonly IReadOnlyList<int> PageSizes = new[] { 10, 25, 50, 100 };

    /// <summary>"Match at least": the percentages the filter offers. 0 = no filter.</summary>
    public static readonly IReadOnlyList<int> MinMatchChoices = new[] { 0, 50, 60, 70, 80, 90 };

    public static readonly IReadOnlyList<string> JobSorts = new[] { SortNewest, SortMatchHigh, SortMatchLow, SortCompany };
    public static readonly IReadOnlyList<string> ReplySorts = new[] { SortNewest, SortMatchHigh, SortMatchLow };

    public static ListQuery Default { get; } = new(1, PageSizes[0], SortNewest, 0);

    public static ListQuery Create(int? page, int? size, string? sort, int? min, IReadOnlyList<string> allowedSorts) => new(
        Math.Max(1, page ?? 1),
        size is { } s && PageSizes.Contains(s) ? s : PageSizes[0],
        sort is not null && allowedSorts.Contains(sort) ? sort : SortNewest,
        min is { } m && MinMatchChoices.Contains(m) ? m : 0);

    /// <summary>
    /// Reads <c>{prefix}_page</c>, <c>{prefix}_size</c>, <c>{prefix}_sort</c> and <c>{prefix}_min</c> from the dashboard's own address,
    /// so a reload (or a copied link) shows the same page of each list.
    /// </summary>
    public static ListQuery FromQuery(IQueryCollection query, string prefix, IReadOnlyList<string> allowedSorts) => Create(
        int.TryParse(query[$"{prefix}_page"], out var page) ? page : null,
        int.TryParse(query[$"{prefix}_size"], out var size) ? size : null,
        query[$"{prefix}_sort"].FirstOrDefault(),
        int.TryParse(query[$"{prefix}_min"], out var min) ? min : null,
        allowedSorts);

    /// <summary>The values as route values, for the address a list polls. What is still the default is left out to keep it short.</summary>
    public RouteValueDictionary ToRoute()
    {
        var route = new RouteValueDictionary();
        if (Page > 1) route["page"] = Page;
        if (PageSize != Default.PageSize) route["size"] = PageSize;
        if (Sort != SortNewest) route["sort"] = Sort;
        if (MinMatch > 0) route["min"] = MinMatch;
        return route;
    }
}

/// <summary>Where a page sits in its list. <paramref name="TotalItems"/> counts what passed the filter, not what exists.</summary>
public sealed record PageInfo(int Page, int PageSize, int TotalItems, int TotalPages, int First, int Last)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}

public static class Paging
{
    /// <summary>One page of a list. A page past the end is the last page, and an empty list is one empty page.</summary>
    public static (IReadOnlyList<T> Items, PageInfo Info) Slice<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        pageSize = Math.Max(1, pageSize);
        var totalPages = Math.Max(1, (items.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, totalPages);

        var skip = (page - 1) * pageSize;
        var slice = items.Skip(skip).Take(pageSize).ToList();
        var info = new PageInfo(page, pageSize, items.Count, totalPages, slice.Count == 0 ? 0 : skip + 1, skip + slice.Count);
        return (slice, info);
    }
}

/// <summary>What the sort / filter / page controls under a list need to draw themselves.</summary>
/// <param name="Unfiltered">How many items the list has before the match filter, to say "3 of 40".</param>
public sealed record ListBar(ListQuery Query, PageInfo Page, IReadOnlyList<(string Value, string Label)> Sorts, string Noun, int Unfiltered)
{
    public bool IsFiltered => Query.MinMatch > 0;
}
