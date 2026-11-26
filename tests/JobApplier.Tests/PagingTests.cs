using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Mail;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace JobApplier.Tests;

public class ListQueryTests
{
    [Fact]
    public void Without_any_values_a_list_starts_on_the_first_page_newest_first_unfiltered()
    {
        var query = ListQuery.Create(null, null, null, null, ListQuery.JobSorts);

        Assert.Equal(new ListQuery(1, 10, "new", 0), query);
        Assert.Equal(ListQuery.Default, query);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void A_page_number_below_one_is_the_first_page(int asked, int expected)
    {
        Assert.Equal(expected, ListQuery.Create(asked, null, null, null, ListQuery.JobSorts).Page);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(25, 25)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(7, 10)]
    [InlineData(1000, 10)]     // nobody can ask for a 1,000-row page by editing the address
    [InlineData(-1, 10)]
    public void Only_the_page_sizes_the_page_offers_are_accepted(int asked, int expected)
    {
        Assert.Equal(expected, ListQuery.Create(null, asked, null, null, ListQuery.JobSorts).PageSize);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(70, 70)]
    [InlineData(90, 90)]
    [InlineData(55, 0)]
    [InlineData(-5, 0)]
    [InlineData(1000, 0)]
    public void Only_the_match_filters_the_page_offers_are_accepted(int asked, int expected)
    {
        Assert.Equal(expected, ListQuery.Create(null, null, null, asked, ListQuery.JobSorts).MinMatch);
    }

    [Theory]
    [InlineData("match-desc", "match-desc")]
    [InlineData("company", "company")]
    [InlineData("nonsense", "new")]
    [InlineData("", "new")]
    [InlineData(null, "new")]
    public void An_unknown_sort_is_newest_first(string? asked, string expected)
    {
        Assert.Equal(expected, ListQuery.Create(null, null, asked, null, ListQuery.JobSorts).Sort);
    }

    [Fact]
    public void The_replies_list_has_no_company_sort()
    {
        Assert.Equal("new", ListQuery.Create(null, null, "company", null, ListQuery.ReplySorts).Sort);
        Assert.Equal("match-asc", ListQuery.Create(null, null, "match-asc", null, ListQuery.ReplySorts).Sort);
    }

    [Fact]
    public void Each_list_reads_only_its_own_prefixed_values_from_the_dashboard_address()
    {
        var address = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["jobs_page"] = "3", ["jobs_size"] = "25", ["jobs_sort"] = "match-desc", ["jobs_min"] = "70",
            ["replies_page"] = "2", ["replies_sort"] = "match-asc"
        });

        Assert.Equal(new ListQuery(3, 25, "match-desc", 70), ListQuery.FromQuery(address, "jobs", ListQuery.JobSorts));
        Assert.Equal(new ListQuery(2, 10, "match-asc", 0), ListQuery.FromQuery(address, "replies", ListQuery.ReplySorts));
    }

    [Fact]
    public void Values_that_are_not_numbers_in_the_address_are_ignored()
    {
        var address = new QueryCollection(new Dictionary<string, StringValues> { ["jobs_page"] = "abc", ["jobs_size"] = "", ["jobs_min"] = "x" });

        Assert.Equal(ListQuery.Default, ListQuery.FromQuery(address, "jobs", ListQuery.JobSorts));
    }

    [Fact]
    public void The_poll_address_carries_only_what_differs_from_the_defaults()
    {
        Assert.Empty(ListQuery.Default.ToRoute());

        var route = new ListQuery(3, 25, "match-desc", 70).ToRoute();
        Assert.Equal(3, route["page"]);
        Assert.Equal(25, route["size"]);
        Assert.Equal("match-desc", route["sort"]);
        Assert.Equal(70, route["min"]);
    }
}

public class PagingSliceTests
{
    private static IReadOnlyList<int> Items(int count) => Enumerable.Range(1, count).ToList();

    [Fact]
    public void An_empty_list_is_one_empty_page()
    {
        var (items, info) = Paging.Slice(Items(0), 1, 10);

        Assert.Empty(items);
        Assert.Equal(new PageInfo(1, 10, 0, 1, 0, 0), info);
        Assert.False(info.HasPrevious);
        Assert.False(info.HasNext);
    }

    [Theory]
    [InlineData(1, 10, 1, 10)]
    [InlineData(2, 10, 11, 20)]
    [InlineData(3, 10, 21, 25)]   // the last page is short
    public void A_page_says_which_items_it_shows(int page, int count, int first, int last)
    {
        var (items, info) = Paging.Slice(Items(25), page, 10);

        Assert.Equal(Enumerable.Range(first, last - first + 1), items);
        Assert.Equal(3, info.TotalPages);
        Assert.Equal(first, info.First);
        Assert.Equal(last, info.Last);
        Assert.Equal(25, info.TotalItems);
        Assert.Equal(page > 1, info.HasPrevious);
        Assert.Equal(page < 3, info.HasNext);
        Assert.True(items.Count <= count);
    }

    [Theory]
    [InlineData(99, 3)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void A_page_that_does_not_exist_becomes_the_nearest_one(int asked, int expected)
    {
        Assert.Equal(expected, Paging.Slice(Items(25), asked, 10).Info.Page);
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(20, 2)]
    [InlineData(21, 3)]
    public void The_page_count_rounds_up(int items, int pages)
    {
        Assert.Equal(pages, Paging.Slice(Items(items), 1, 10).Info.TotalPages);
    }
}

public class JobListTests
{
    private static JobRecord Job(string company, int? percent, JobStatus status = JobStatus.Applied, int minutesAgo = 0) => new()
    {
        Status = status,
        CreatedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
        Extracted = new ExtractedJob { Company = company, Title = "Developer" },
        Match = percent is int p ? new MatchResult { SkillsScore = p, ExperienceScore = p, DomainScore = p, SeniorityScore = p, LogisticsScore = p } : null
    };

    private static JobStore Store(TestEnv env, params JobRecord[] jobs)
    {
        var store = env.NewStore();
        foreach (var job in jobs) store.Add(job);
        return store;
    }

    private static string[] Companies(LiveViewModel model) => model.Rows.Select(r => r.Company).ToArray();

    [Fact]
    public void The_dashboard_shows_one_page_of_jobs_newest_first_and_says_where_it_is()
    {
        using var env = new TestEnv();
        var store = Store(env, Enumerable.Range(0, 25).Select(i => Job("Co" + i, 50, minutesAgo: i)).ToArray());   // Co0 is the newest

        var first = LiveViewModelBuilder.Build(store);
        Assert.Equal(10, first.Rows.Count);
        Assert.Equal("Co0", first.Rows[0].Company);
        Assert.Equal(new PageInfo(1, 10, 25, 3, 1, 10), first.Page);

        var last = LiveViewModelBuilder.Build(store, query: new ListQuery(3, 10, "new", 0));
        Assert.Equal(5, last.Rows.Count);
        Assert.Equal("Co24", last.Rows[^1].Company);
        Assert.Equal(21, last.Page.First);
        Assert.Equal(25, last.Page.Last);
    }

    [Fact]
    public void A_page_past_the_end_shows_the_last_page_and_says_so()
    {
        using var env = new TestEnv();
        var store = Store(env, Enumerable.Range(0, 25).Select(i => Job("Co" + i, 50, minutesAgo: i)).ToArray());

        var model = LiveViewModelBuilder.Build(store, query: new ListQuery(99, 10, "new", 0));

        Assert.Equal(3, model.Page.Page);
        Assert.Equal(3, model.Query.Page);   // the corrected page, so the controls and the poll address do not point past the end
        Assert.Equal(5, model.Rows.Count);
    }

    [Fact]
    public void Sorting_by_match_puts_the_best_first_and_jobs_without_a_score_last()
    {
        using var env = new TestEnv();
        var store = Store(env,
            Job("Mid", 60, minutesAgo: 1), Job("Unscored", null, minutesAgo: 2), Job("High", 90, minutesAgo: 3), Job("Low", 30, minutesAgo: 4),
            Job("MidTie", 60, minutesAgo: 0));   // same percentage as Mid, but newer

        var high = LiveViewModelBuilder.Build(store, query: ListQuery.Default with { Sort = "match-desc" });
        Assert.Equal(new[] { "High", "MidTie", "Mid", "Low", "Unscored" }, Companies(high));

        var low = LiveViewModelBuilder.Build(store, query: ListQuery.Default with { Sort = "match-asc" });
        Assert.Equal(new[] { "Low", "MidTie", "Mid", "High", "Unscored" }, Companies(low));
    }

    [Fact]
    public void Sorting_by_company_ignores_case()
    {
        using var env = new TestEnv();
        var store = Store(env, Job("zeta", 50), Job("Alpha", 50), Job("beta", 50));

        Assert.Equal(new[] { "Alpha", "beta", "zeta" }, Companies(LiveViewModelBuilder.Build(store, query: ListQuery.Default with { Sort = "company" })));
    }

    [Fact]
    public void The_match_filter_keeps_only_jobs_at_or_above_it_and_leaves_out_unscored_ones()
    {
        using var env = new TestEnv();
        var store = Store(env, Job("A", 95), Job("B", 70), Job("C", 69), Job("D", null), Job("E", 20));

        var model = LiveViewModelBuilder.Build(store, query: ListQuery.Default with { MinMatch = 70, Sort = "match-desc" });

        Assert.Equal(new[] { "A", "B" }, Companies(model));
        Assert.Equal(2, model.Page.TotalItems);       // after the filter...
        Assert.Equal(5, model.Counts.Total);           // ...while the totals are about every job
    }

    [Fact]
    public void Filtering_and_sorting_happen_before_the_pages_are_cut()
    {
        using var env = new TestEnv();
        var store = Store(env, Enumerable.Range(0, 30).Select(i => Job("Co" + i, 40 + i * 2, minutesAgo: i)).ToArray());   // 30 jobs scored 40, 42, ... 98

        var page2 = LiveViewModelBuilder.Build(store, query: new ListQuery(2, 10, "match-desc", 60));

        // scores >= 60: 60..98 = 20 jobs; best first, so page 2 is the lower half: 78 down to 60
        Assert.Equal(20, page2.Page.TotalItems);
        Assert.Equal(10, page2.Rows.Count);
        Assert.Equal(78, page2.Rows[0].MatchPercent);
        Assert.Equal(60, page2.Rows[^1].MatchPercent);
    }

    [Fact]
    public void The_totals_and_the_retry_counts_cover_every_job_not_just_the_page()
    {
        using var env = new TestEnv();
        var jobs = Enumerable.Range(0, 12).Select(i => Job("Failed" + i, null, JobStatus.Failed, minutesAgo: i))
            .Concat(Enumerable.Range(0, 7).Select(i => Job("Done" + i, 80, JobStatus.Applied, minutesAgo: 20 + i)))
            .Concat(new[] { Job("Busy", null, JobStatus.Applying, minutesAgo: 40) }).ToArray();
        var store = Store(env, jobs);

        var model = LiveViewModelBuilder.Build(store);   // page 1 of 2: ten rows

        Assert.Equal(10, model.Rows.Count);
        Assert.Equal(20, model.Counts.Total);
        Assert.Equal(12, model.Counts.FailedAll);   // "Retry all failed (12)", although only 10 rows are shown
        Assert.Equal(12, model.Counts.Failed);
        Assert.Equal(7, model.Counts.Applied);
        Assert.Equal(1, model.Counts.Running);
    }

    [Fact]
    public void A_failed_job_waiting_for_the_usage_limit_is_counted_as_waiting_not_as_one_to_clear()
    {
        using var env = new TestEnv();
        var waiting = Job("Waiting", null, JobStatus.Failed);
        waiting.LimitHit = true;
        waiting.AutoRetry = true;
        var limited = Job("Limited", null, JobStatus.Failed);
        limited.LimitHit = true;
        var plain = Job("Plain", null, JobStatus.Failed);

        var counts = LiveViewModelBuilder.Build(Store(env, waiting, limited, plain)).Counts;

        Assert.Equal(3, counts.FailedAll);
        Assert.Equal(1, counts.Waiting);
        Assert.Equal(1, counts.Limited);
        Assert.Equal(2, counts.Failed);
    }

    [Fact]
    public void An_empty_dashboard_is_one_empty_page()
    {
        using var env = new TestEnv();

        var model = LiveViewModelBuilder.Build(Store(env));

        Assert.Empty(model.Rows);
        Assert.Equal(0, model.Counts.Total);
        Assert.Equal(1, model.Page.TotalPages);
    }
}

public class ReplyListTests
{
    private sealed class Rig : IDisposable
    {
        public TestEnv Env { get; } = new();
        public JobStore Jobs { get; }
        public ReplyStore Replies { get; }
        public MailChecker Checker { get; }
        public AccountService Accounts { get; }

        public Rig()
        {
            Jobs = Env.NewStore();
            Replies = new ReplyStore(Env.Paths);
            var settings = new SettingsService(Env.Paths);
            Accounts = new AccountService(Env.Paths, settings, NullLogger<AccountService>.Instance);
            Checker = new MailChecker(new FakeReader(), Accounts, Replies, NullLogger<MailChecker>.Instance);
        }

        /// <summary>A job with this match percentage (none = never scored) and a reply to it.</summary>
        public EmployerReply Reply(string company, int? percent, ReplyKind kind = ReplyKind.Acknowledged, int minutesAgo = 0)
        {
            var job = new JobRecord
            {
                Extracted = new ExtractedJob { Company = company },
                Match = percent is int p ? new MatchResult { SkillsScore = p, ExperienceScore = p, DomainScore = p, SeniorityScore = p, LogisticsScore = p } : null
            };
            Jobs.Add(job);
            var reply = new EmployerReply
            {
                MessageId = "m-" + company, JobId = job.Id, ApplicationId = Guid.NewGuid(), Company = company, Kind = kind,
                ReceivedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo)
            };
            Replies.Upsert(new[] { reply });
            return reply;
        }

        public RepliesViewModel Build(ListQuery? query = null) => RepliesViewModelBuilder.Build(Replies, Checker, Accounts, Jobs, query);

        public void Dispose() => Env.Dispose();
    }

    private static string[] Companies(RepliesViewModel model) => model.Replies.Select(r => r.Company).ToArray();

    [Fact]
    public void Replies_are_listed_newest_first_a_page_at_a_time()
    {
        using var rig = new Rig();
        for (var i = 0; i < 25; i++) rig.Reply("Co" + i, 50, minutesAgo: i);   // Co0 is the newest

        var first = rig.Build();
        Assert.Equal(10, first.Replies.Count);
        Assert.Equal("Co0", first.Replies[0].Company);
        Assert.Equal(25, first.Total);
        Assert.Equal(3, first.Page.TotalPages);

        var last = rig.Build(new ListQuery(3, 10, "new", 0));
        Assert.Equal(5, last.Replies.Count);
        Assert.Equal("Co24", last.Replies[^1].Company);
    }

    [Fact]
    public void Replies_can_be_sorted_by_the_match_of_the_job_they_answer()
    {
        using var rig = new Rig();
        rig.Reply("Mid", 60, minutesAgo: 1);
        rig.Reply("Unscored", null, minutesAgo: 2);
        rig.Reply("High", 90, minutesAgo: 3);
        rig.Reply("Low", 30, minutesAgo: 4);

        Assert.Equal(new[] { "High", "Mid", "Low", "Unscored" }, Companies(rig.Build(ListQuery.Default with { Sort = "match-desc" })));
        Assert.Equal(new[] { "Low", "Mid", "High", "Unscored" }, Companies(rig.Build(ListQuery.Default with { Sort = "match-asc" })));
    }

    [Fact]
    public void A_reply_to_a_job_that_was_deleted_has_no_match_and_sorts_last()
    {
        using var rig = new Rig();
        rig.Reply("High", 90);
        rig.Replies.Upsert(new[] { new EmployerReply { MessageId = "orphan", JobId = Guid.NewGuid(), Company = "Gone", ReceivedUtc = DateTime.UtcNow } });

        var model = rig.Build(ListQuery.Default with { Sort = "match-desc" });

        Assert.Equal(new[] { "High", "Gone" }, Companies(model));
        Assert.False(model.MatchByJob.ContainsKey(model.Replies[1].JobId));
    }

    [Fact]
    public void The_match_filter_applies_to_replies_too()
    {
        using var rig = new Rig();
        rig.Reply("A", 95); rig.Reply("B", 70); rig.Reply("C", 69); rig.Reply("D", null);

        var model = rig.Build(ListQuery.Default with { MinMatch = 70, Sort = "match-desc" });

        Assert.Equal(new[] { "A", "B" }, Companies(model));
        Assert.Equal(2, model.Page.TotalItems);
        Assert.Equal(4, model.Total);
    }

    [Fact]
    public void The_kind_chips_count_every_reply_whatever_the_page_and_the_filter()
    {
        using var rig = new Rig();
        for (var i = 0; i < 12; i++) rig.Reply("R" + i, 40, ReplyKind.Rejected, i);
        rig.Reply("Offer", 99, ReplyKind.Offer);
        rig.Reply("Ack", 10, ReplyKind.Acknowledged);

        var model = rig.Build(new ListQuery(1, 10, "new", 90));   // only the offer passes the filter

        Assert.Single(model.Replies);
        Assert.Equal(12, model.Count(ReplyKind.Rejected));
        Assert.Equal(1, model.Count(ReplyKind.Offer));
        Assert.Equal(1, model.Count(ReplyKind.Acknowledged));
        Assert.Equal(0, model.Count(ReplyKind.InfoRequested));
        Assert.Equal(14, model.Total);
    }

    [Fact]
    public void A_page_past_the_end_shows_the_last_page()
    {
        using var rig = new Rig();
        for (var i = 0; i < 12; i++) rig.Reply("Co" + i, 50, minutesAgo: i);

        var model = rig.Build(new ListQuery(9, 10, "new", 0));

        Assert.Equal(2, model.Page.Page);
        Assert.Equal(2, model.Replies.Count);
    }
}

public class ListEndpointTests
{
    private sealed class StubLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public void The_replies_endpoint_pages_sorts_and_filters_from_its_query_and_ignores_nonsense()
    {
        using var env = new TestEnv();
        var jobs = env.NewStore();
        var replies = new ReplyStore(env.Paths);
        var settings = new SettingsService(env.Paths);
        var accounts = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
        var checker = new MailChecker(new FakeReader(), accounts, replies, NullLogger<MailChecker>.Instance);
        for (var i = 0; i < 25; i++)
        {
            var job = new JobRecord { Match = new MatchResult { SkillsScore = 40 + i, ExperienceScore = 40 + i, DomainScore = 40 + i, SeniorityScore = 40 + i, LogisticsScore = 40 + i } };
            jobs.Add(job);
            replies.Upsert(new[] { new EmployerReply { MessageId = "m" + i, JobId = job.Id, Company = "Co" + i, ReceivedUtc = DateTime.UtcNow.AddMinutes(-i) } });
        }
        var composer = new ReplyComposer(new FakeReader(), replies, new ProfileService(env.Paths), NullLogger<ReplyComposer>.Instance);
        var controller = new GmailController(new FakeReader(), checker, replies, accounts, jobs, settings, composer, new StubLifetime(), NullLogger<GmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var asked = Assert.IsType<RepliesViewModel>(Assert.IsType<PartialViewResult>(controller.Replies(2, 10, "match-desc", 50)).Model);
        Assert.Equal(2, asked.Page.Page);
        Assert.Equal("match-desc", asked.Query.Sort);
        Assert.Equal(50, asked.Query.MinMatch);
        Assert.Equal(15, asked.Page.TotalItems);                       // scores 50..64 are the ones at or above 50 (40 + i, i = 10..24)
        Assert.Equal(new[] { 54, 53, 52, 51, 50 }, asked.Replies.Select(r => asked.MatchByJob[r.JobId]).ToArray());   // page 2 of the best-first list

        var nonsense = Assert.IsType<RepliesViewModel>(Assert.IsType<PartialViewResult>(controller.Replies(-3, 9999, "drop table", 12345)).Model);
        Assert.Equal(ListQuery.Default, nonsense.Query);
        Assert.Equal(10, nonsense.Replies.Count);
    }
}
