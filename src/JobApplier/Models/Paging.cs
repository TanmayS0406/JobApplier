using JobApplier.Mail;
using Microsoft.AspNetCore.Routing;

namespace JobApplier.Models;

/// <summary>
/// How a list on the dashboard is paged, sorted and filtered. It comes from the query string, so every value is clamped to what the
/// page actually offers: a hand-edited address can never ask for a 10,000-row page or an unknown sort.
/// </summary>
/// <param name="Show">Which rows to keep, by what the employer answered or how the job ended ("rejected", "info", "skipped", ...). Empty = all.</param>
public sealed record ListQuery(int Page, int PageSize, string Sort, int MinMatch, string Show = "")
{
    public const string SortNewest = "new";
    public const string SortMatchHigh = "match-desc";
    public const string SortMatchLow = "match-asc";
    public const string SortCompany = "company";

    // What "Show" can ask for. A job list and a reply list offer different ones (see JobShows and ReplyShows).
    public const string ShowReplied = "replied";
    public const string ShowNoReply = "noreply";
    public const string ShowSkipped = "skipped";
    public const string ShowOffer = "offer";
    public const string ShowMoving = "moving";
    public const string ShowInfo = "info";
    public const string ShowRejected = "rejected";
    public const string ShowReceived = "received";
    public const string ShowUnclear = "unclear";

    public static readonly IReadOnlyList<int> PageSizes = new[] { 10, 25, 50, 100 };

    /// <summary>"Match at least": the percentages the filter offers. 0 = no filter.</summary>
    public static readonly IReadOnlyList<int> MinMatchChoices = new[] { 0, 50, 60, 70, 80, 90 };

    public static readonly IReadOnlyList<string> JobSorts = new[] { SortNewest, SortMatchHigh, SortMatchLow, SortCompany };
    public static readonly IReadOnlyList<string> ReplySorts = new[] { SortNewest, SortMatchHigh, SortMatchLow };

    /// <summary>The results table: jobs whose employer wrote back (or a particular kind of answer), jobs still waiting for one, and jobs that were skipped.</summary>
    public static readonly IReadOnlyList<string> JobShows = new[] { ShowReplied, ShowRejected, ShowInfo, ShowMoving, ShowOffer, ShowNoReply, ShowSkipped };

    /// <summary>The replies card: one kind of answer.</summary>
    public static readonly IReadOnlyList<string> ReplyShows = new[] { ShowOffer, ShowMoving, ShowInfo, ShowRejected, ShowReceived, ShowUnclear };

    /// <summary>The kind of answer a "Show" value asks for, or null when it is not about the kind of answer (all, replied, no reply, skipped).</summary>
    public static ReplyKind? KindOf(string show) => show switch
    {
        ShowOffer => ReplyKind.Offer,
        ShowMoving => ReplyKind.MovingForward,
        ShowInfo => ReplyKind.InfoRequested,
        ShowRejected => ReplyKind.Rejected,
        ShowReceived => ReplyKind.Acknowledged,
        ShowUnclear => ReplyKind.Unclear,
        _ => null
    };

    public static ListQuery Default { get; } = new(1, PageSizes[0], SortNewest, 0);

    public static ListQuery Create(int? page, int? size, string? sort, int? min, IReadOnlyList<string> allowedSorts,
                                   string? show = null, IReadOnlyList<string>? allowedShows = null) => new(
        Math.Max(1, page ?? 1),
        size is { } s && PageSizes.Contains(s) ? s : PageSizes[0],
        sort is not null && allowedSorts.Contains(sort) ? sort : SortNewest,
        min is { } m && MinMatchChoices.Contains(m) ? m : 0,
        show is not null && allowedShows != null && allowedShows.Contains(show) ? show : "");

    /// <summary>
    /// Reads <c>{prefix}_page</c>, <c>{prefix}_size</c>, <c>{prefix}_sort</c>, <c>{prefix}_min</c> and <c>{prefix}_show</c> from the dashboard's
    /// own address, so a reload (or a copied link) shows the same page of each list.
    /// </summary>
    public static ListQuery FromQuery(IQueryCollection query, string prefix, IReadOnlyList<string> allowedSorts, IReadOnlyList<string>? allowedShows = null) => Create(
        int.TryParse(query[$"{prefix}_page"], out var page) ? page : null,
        int.TryParse(query[$"{prefix}_size"], out var size) ? size : null,
        query[$"{prefix}_sort"].FirstOrDefault(),
        int.TryParse(query[$"{prefix}_min"], out var min) ? min : null,
        allowedSorts,
        query[$"{prefix}_show"].FirstOrDefault(),
        allowedShows);

    /// <summary>The values as route values, for the address a list polls. What is still the default is left out to keep it short.</summary>
    public RouteValueDictionary ToRoute()
    {
        var route = new RouteValueDictionary();
        if (Page > 1) route["page"] = Page;
        if (PageSize != Default.PageSize) route["size"] = PageSize;
        if (Sort != SortNewest) route["sort"] = Sort;
        if (MinMatch > 0) route["min"] = MinMatch;
        if (Show.Length > 0) route["show"] = Show;
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
/// <param name="Shows">The choices of the "Show" filter (value and label; the empty value is added as "All" by the view). Null: the list has no such filter.</param>
public sealed record ListBar(ListQuery Query, PageInfo Page, IReadOnlyList<(string Value, string Label)> Sorts, string Noun, int Unfiltered,
                             IReadOnlyList<(string Value, string Label)>? Shows = null)
{
    public bool IsFiltered => Query.MinMatch > 0 || Query.Show.Length > 0;

    /// <summary>What the filter is set to, in words, for "Showing 1-3 of 3 (rejected, match at least 70%, 40 in total)".</summary>
    public string FilterText
    {
        get
        {
            var parts = new List<string>();
            if (Query.Show.Length > 0) parts.Add((Shows?.FirstOrDefault(s => s.Value == Query.Show).Label ?? Query.Show).ToLowerInvariant());
            if (Query.MinMatch > 0) parts.Add($"match at least {Query.MinMatch}%");
            return string.Join(", ", parts);
        }
    }
}
