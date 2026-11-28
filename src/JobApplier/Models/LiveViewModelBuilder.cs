using JobApplier.Domain;
using JobApplier.Mail;
using JobApplier.Services;

namespace JobApplier.Models;

public static class LiveViewModelBuilder
{
    public static LiveViewModel Build(JobStore store, DateTime? pausedUntilUtc = null, ReplyStore? replies = null, ListQuery? query = null, bool heldByUser = false)
    {
        query ??= ListQuery.Default;

        var rows = store.ReadAll(JobRow.From);   // newest first
        var help = store
            .ReadAll(r => r.PendingHelp == null ? null : new HelpItem(r.Id, r.DisplayCompany, r.DisplayTitle, r.PendingHelp))
            .OfType<HelpItem>()
            .ToList();

        var latestReply = replies?.StatusByJob() ?? new Dictionary<Guid, EmployerReply>();
        var (page, info) = Paging.Slice(Sort(Filter(rows, query.MinMatch, query.Show, latestReply, query.Search), query.Sort), query.Page, query.PageSize);
        return new LiveViewModel
        {
            Rows = page,
            Counts = JobCounts.From(rows),
            Query = query with { Page = info.Page },
            Page = info,
            Help = help,
            PausedUntilUtc = pausedUntilUtc,
            HeldByUser = heldByUser,
            RepliesTracked = replies?.State().Enabled ?? false,
            Replies = latestReply
        };
    }

    /// <summary>
    /// "Match at least 70%": a job that has not been scored yet has no match to compare, so it is left out while a filter is on.
    /// "Show": by what the employer answered (the same latest answer the Employer reply column shows), by no answer yet, or by being skipped.
    /// </summary>
    internal static IReadOnlyList<JobRow> Filter(IReadOnlyList<JobRow> rows, int minMatch, string show = "",
                                                 IReadOnlyDictionary<Guid, EmployerReply>? latestReply = null, string search = "")
    {
        latestReply ??= new Dictionary<Guid, EmployerReply>();
        IEnumerable<JobRow> kept = rows;

        if (minMatch > 0) kept = kept.Where(r => r.MatchPercent >= minMatch);

        if (show.Length > 0)
        {
            kept = show switch
            {
                ListQuery.ShowReplied => kept.Where(r => latestReply.ContainsKey(r.Id)),
                ListQuery.ShowNoReply => kept.Where(r => r.Status == JobStatus.Applied && !latestReply.ContainsKey(r.Id)),
                ListQuery.ShowSkipped => kept.Where(r => r.Status == JobStatus.Skipped),
                _ when ListQuery.KindOf(show) is { } kind => kept.Where(r => latestReply.TryGetValue(r.Id, out var reply) && reply.Kind == kind),
                _ => kept
            };
        }
        if (search.Length > 0) kept = kept.Where(r => r.Company.Contains(search, StringComparison.OrdinalIgnoreCase));
        return kept as IReadOnlyList<JobRow> ?? kept.ToList();
    }

    /// <summary>Jobs without a match percentage always come last, whichever way the percentage is sorted. Ties keep newest first.</summary>
    internal static IReadOnlyList<JobRow> Sort(IReadOnlyList<JobRow> rows, string sort) => sort switch
    {
        ListQuery.SortMatchHigh => rows.OrderByDescending(r => r.MatchPercent.HasValue).ThenByDescending(r => r.MatchPercent).ThenByDescending(r => r.CreatedUtc).ToList(),
        ListQuery.SortMatchLow => rows.OrderByDescending(r => r.MatchPercent.HasValue).ThenBy(r => r.MatchPercent).ThenByDescending(r => r.CreatedUtc).ToList(),
        ListQuery.SortCompany => rows.OrderBy(r => r.Company, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.CreatedUtc).ToList(),
        _ => rows   // newest first, as the store returns them
    };
}
