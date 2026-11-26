using JobApplier.Domain;
using JobApplier.Mail;
using JobApplier.Services;

namespace JobApplier.Models;

public static class RepliesViewModelBuilder
{
    public static RepliesViewModel Build(ReplyStore replies, MailChecker checker, AccountService accounts, JobStore jobs, ListQuery? query = null)
    {
        query ??= ListQuery.Default;
        var state = replies.State();
        var all = replies.All();   // newest first

        // The match percentage lives on the job, not on the mail: a reply is as good a match as the job it answers.
        var matchByJob = jobs.ReadAll(r => (r.Id, Percent: r.Match?.Percent))
            .Where(j => j.Percent.HasValue)
            .ToDictionary(j => j.Id, j => j.Percent!.Value);

        var (page, info) = Paging.Slice(Sort(Filter(all, matchByJob, query.MinMatch, query.Show), matchByJob, query.Sort), query.Page, query.PageSize);
        return new RepliesViewModel
        {
            Enabled = state.Enabled,
            Checking = checker.IsRunning,
            ApplicationCount = accounts.Applications().Count(a => a.Result is ApplyResultKind.Submitted or ApplyResultKind.AlreadyApplied),
            LastCheckedUtc = state.LastCheckedUtc,
            LastError = state.LastError,
            Total = all.Count,
            KindCounts = all.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()),
            MatchByJob = matchByJob,
            Query = query with { Page = info.Page },
            Page = info,
            Replies = page
        };
    }

    /// <summary>
    /// "Match at least 70%": a reply whose job has no match percentage (never scored, or deleted since) is left out while a filter is on.
    /// "Show": only one kind of answer (rejected, information requested, ...).
    /// </summary>
    internal static IReadOnlyList<EmployerReply> Filter(IReadOnlyList<EmployerReply> replies, IReadOnlyDictionary<Guid, int> matchByJob, int minMatch, string show = "")
    {
        IEnumerable<EmployerReply> kept = replies;
        if (minMatch > 0) kept = kept.Where(r => matchByJob.TryGetValue(r.JobId, out var match) && match >= minMatch);
        if (ListQuery.KindOf(show) is { } kind) kept = kept.Where(r => r.Kind == kind);
        return kept as IReadOnlyList<EmployerReply> ?? kept.ToList();
    }

    /// <summary>Replies without a match percentage come last, whichever way the percentage is sorted. Ties keep newest first.</summary>
    internal static IReadOnlyList<EmployerReply> Sort(IReadOnlyList<EmployerReply> replies, IReadOnlyDictionary<Guid, int> matchByJob, string sort)
    {
        int? Match(EmployerReply r) => matchByJob.TryGetValue(r.JobId, out var match) ? match : null;

        return sort switch
        {
            ListQuery.SortMatchHigh => replies.OrderByDescending(r => Match(r).HasValue).ThenByDescending(Match).ThenByDescending(r => r.ReceivedUtc).ToList(),
            ListQuery.SortMatchLow => replies.OrderByDescending(r => Match(r).HasValue).ThenBy(Match).ThenByDescending(r => r.ReceivedUtc).ToList(),
            _ => replies.OrderByDescending(r => r.ReceivedUtc).ToList()
        };
    }
}
