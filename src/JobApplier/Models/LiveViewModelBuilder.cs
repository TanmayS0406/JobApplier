using JobApplier.Mail;
using JobApplier.Services;

namespace JobApplier.Models;

public static class LiveViewModelBuilder
{
    public static LiveViewModel Build(JobStore store, DateTime? pausedUntilUtc = null, ReplyStore? replies = null, ListQuery? query = null)
    {
        query ??= ListQuery.Default;

        var rows = store.ReadAll(JobRow.From);   // newest first
        var help = store
            .ReadAll(r => r.PendingHelp == null ? null : new HelpItem(r.Id, r.DisplayCompany, r.DisplayTitle, r.PendingHelp))
            .OfType<HelpItem>()
            .ToList();

        var (page, info) = Paging.Slice(Sort(Filter(rows, query.MinMatch), query.Sort), query.Page, query.PageSize);
        return new LiveViewModel
        {
            Rows = page,
            Counts = JobCounts.From(rows),
            Query = query with { Page = info.Page },
            Page = info,
            Help = help,
            PausedUntilUtc = pausedUntilUtc,
            RepliesTracked = replies?.State().Enabled ?? false,
            Replies = replies?.StatusByJob() ?? new Dictionary<Guid, EmployerReply>()
        };
    }

    /// <summary>"Match at least 70%": a job that has not been scored yet has no match to compare, so it is left out while a filter is on.</summary>
    internal static IReadOnlyList<JobRow> Filter(IReadOnlyList<JobRow> rows, int minMatch) =>
        minMatch <= 0 ? rows : rows.Where(r => r.MatchPercent >= minMatch).ToList();

    /// <summary>Jobs without a match percentage always come last, whichever way the percentage is sorted. Ties keep newest first.</summary>
    internal static IReadOnlyList<JobRow> Sort(IReadOnlyList<JobRow> rows, string sort) => sort switch
    {
        ListQuery.SortMatchHigh => rows.OrderByDescending(r => r.MatchPercent.HasValue).ThenByDescending(r => r.MatchPercent).ThenByDescending(r => r.CreatedUtc).ToList(),
        ListQuery.SortMatchLow => rows.OrderByDescending(r => r.MatchPercent.HasValue).ThenBy(r => r.MatchPercent).ThenByDescending(r => r.CreatedUtc).ToList(),
        ListQuery.SortCompany => rows.OrderBy(r => r.Company, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.CreatedUtc).ToList(),
        _ => rows   // newest first, as the store returns them
    };
}
