using JobApplier.Domain;
using JobApplier.Models;
using JobApplier.Services;

namespace JobApplier.Tests;

public class JobSearchTests
{
    [Fact]
    public void The_search_keeps_only_jobs_whose_company_contains_the_text_and_counts_all_jobs()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        store.Add(new JobRecord { Extracted = new ExtractedJob { Company = "Acme Corp", Title = "Dev" } });
        store.Add(new JobRecord { Extracted = new ExtractedJob { Company = "Globex", Title = "Dev" } });

        var model = LiveViewModelBuilder.Build(store, null, null, ListQuery.Default with { Search = "acme" });

        Assert.Equal(new[] { "Acme Corp" }, model.Rows.Select(r => r.Company));
        Assert.Equal(2, model.Counts.Total);   // the totals still cover every job
    }

    [Fact]
    public void The_search_is_trimmed_limited_and_kept_in_the_poll_address()
    {
        var query = ListQuery.Create(null, null, null, null, ListQuery.JobSorts, null, null, "  acme  ");

        Assert.Equal("acme", query.Search);
        Assert.Equal("acme", query.ToRoute()["q"]);
        Assert.Equal(80, ListQuery.Create(null, null, null, null, ListQuery.JobSorts, null, null, new string('x', 200)).Search.Length);
        Assert.False(ListQuery.Default.ToRoute().ContainsKey("q"));
    }
}
