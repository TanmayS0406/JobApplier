using System.Text.Json;
using JobApplier.Agents;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Mcp;
using JobApplier.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace JobApplier.Tests;

public class VisaRelocationOptionTests
{
    [Fact]
    public void The_choice_is_part_of_what_the_retry_dialog_posts_and_is_off_when_not_posted()
    {
        Assert.True(RetryOptions.From(true, autoSubmit: false, minMatch: 50, requireVisaOrRelocation: true)!.RequireVisaOrRelocation);
        Assert.False(RetryOptions.From(true, autoSubmit: false, minMatch: 50)!.RequireVisaOrRelocation);   // an unticked checkbox posts nothing
        Assert.Null(RetryOptions.From(false, true, 50, requireVisaOrRelocation: true));                        // no dialog, nothing chosen
    }

    [Fact]
    public void The_choice_is_described_in_plain_words_only_when_it_is_on()
    {
        Assert.Equal("submits automatically, skips below 70%, only if it offers visa sponsorship or relocation",
            new RetryOptions(true, 70, true).Describe());
        Assert.Equal("waits for your approval before submitting, no minimum match", new RetryOptions(false, 0).Describe());
    }

    [Fact]
    public void A_job_that_asks_for_nothing_extra_has_the_option_off_and_so_does_a_default_install()
    {
        Assert.False(new JobOptions().RequireVisaOrRelocation);
        Assert.False(new AppSettings().DefaultRequireVisaOrRelocation);
    }

    [Fact]
    public void The_default_is_saved_with_the_settings()
    {
        using var env = new TestEnv();
        var service = new SettingsService(env.Paths);

        Assert.Null(service.Save(new AppSettings { DefaultRequireVisaOrRelocation = true }));

        Assert.True(new SettingsService(env.Paths).Current.DefaultRequireVisaOrRelocation);
    }
}

public class VisaRelocationGateTests
{
    private static JobRecord Scored(Rig rig, bool require, SupportLevel visa, SupportLevel relocation, bool assessed = true, int minMatch = 0, int score = 80, JobStatus status = JobStatus.Queued)
    {
        var job = new JobRecord
        {
            Status = status,
            Options = new JobOptions { RequireVisaOrRelocation = require, MinMatchPercent = minMatch },
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" },
            Found = new FoundPosting { Found = true, Url = "https://acme.example/jobs/1" },
            Match = new MatchResult
            {
                SkillsScore = score, ExperienceScore = score, DomainScore = score, SeniorityScore = score, LogisticsScore = score,
                VisaSponsorship = visa, Relocation = relocation, SupportAssessed = assessed
            }
        };
        rig.Store.Add(job);
        return job;
    }

    private static async Task<StageResult> RunMatch(Rig rig, JobRecord job, PipelineServices? pipeline = null)
    {
        var profile = ProfileService.Clone(rig.Profile.Current);
        profile.ResumeText = "My resume";
        rig.Profile.Save(profile);
        return await new MatchExecutor(pipeline ?? rig.Pipeline).HandleAsync(new StageResult(job.Id, true), null!, CancellationToken.None);
    }

    [Theory]
    [InlineData(SupportLevel.Offered, SupportLevel.NotMentioned, true)]
    [InlineData(SupportLevel.NotMentioned, SupportLevel.Offered, true)]
    [InlineData(SupportLevel.Offered, SupportLevel.NotOffered, true)]      // one offer is enough
    [InlineData(SupportLevel.NotOffered, SupportLevel.Offered, true)]
    [InlineData(SupportLevel.NotMentioned, SupportLevel.NotMentioned, false)]
    [InlineData(SupportLevel.NotOffered, SupportLevel.NotOffered, false)]
    [InlineData(SupportLevel.NotOffered, SupportLevel.NotMentioned, false)]
    [InlineData(SupportLevel.NotMentioned, SupportLevel.NotOffered, false)]
    public async Task A_job_goes_on_only_when_the_posting_offers_visa_sponsorship_or_relocation(SupportLevel visa, SupportLevel relocation, bool goesOn)
    {
        using var rig = new Rig();
        var job = Scored(rig, require: true, visa, relocation);

        var result = await RunMatch(rig, job);

        Assert.Equal(goesOn, result.Continue);
        Assert.Equal(goesOn ? JobStatus.Queued : JobStatus.Skipped, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public async Task Without_the_option_the_same_posting_goes_on_whatever_it_says()
    {
        using var rig = new Rig();
        var job = Scored(rig, require: false, SupportLevel.NotOffered, SupportLevel.NotOffered);

        Assert.True((await RunMatch(rig, job)).Continue);
    }

    [Fact]
    public async Task A_skipped_job_says_what_the_posting_said_and_how_to_go_on_anyway()
    {
        using var rig = new Rig();
        var silent = Scored(rig, require: true, SupportLevel.NotMentioned, SupportLevel.NotMentioned);
        var refused = Scored(rig, require: true, SupportLevel.NotOffered, SupportLevel.NotMentioned);

        await RunMatch(rig, silent);
        await RunMatch(rig, refused);

        var silentDetail = rig.Store.Read(silent.Id, r => r.StatusDetail);
        Assert.Contains("does not mention visa sponsorship", silentDetail);
        Assert.Contains("does not mention relocation help", silentDetail);
        Assert.Contains("Apply anyway", silentDetail);
        Assert.Contains("says it does not sponsor visas", rig.Store.Read(refused.Id, r => r.StatusDetail));
        Assert.Contains(rig.Store.Read(silent.Id, r => r.Log.Select(l => l.Text).ToList()), line => line.StartsWith("Skipped: no visa sponsorship or relocation help"));
    }

    [Fact]
    public async Task The_minimum_match_is_judged_first_so_its_reason_is_the_one_given()
    {
        using var rig = new Rig();
        var job = Scored(rig, require: true, SupportLevel.NotMentioned, SupportLevel.NotMentioned, minMatch: 90, score: 50);

        await RunMatch(rig, job);

        Assert.Contains("below your minimum of 90%", rig.Store.Read(job.Id, r => r.StatusDetail));
    }

    [Fact]
    public async Task Apply_anyway_lifts_the_requirement_and_the_job_then_goes_on()
    {
        using var rig = new Rig();
        var job = Scored(rig, require: true, SupportLevel.NotMentioned, SupportLevel.NotMentioned);
        await RunMatch(rig, job);
        Assert.Equal(JobStatus.Skipped, rig.Store.Read(job.Id, r => r.Status));

        Assert.True(rig.Runner.Retry(job.Id, applyAnyway: true));

        Assert.False(rig.Store.Read(job.Id, r => r.Options.RequireVisaOrRelocation));
        Assert.True((await RunMatch(rig, job)).Continue);
    }

    [Fact]
    public void A_retry_with_choices_sets_the_requirement_on_the_job_and_one_without_leaves_it()
    {
        using var rig = new Rig();
        var plain = Scored(rig, require: false, SupportLevel.NotMentioned, SupportLevel.NotMentioned, status: JobStatus.Failed);
        var chosen = Scored(rig, require: false, SupportLevel.NotMentioned, SupportLevel.NotMentioned, status: JobStatus.Failed);

        Assert.True(rig.Runner.Retry(plain.Id));
        Assert.False(rig.Store.Read(plain.Id, r => r.Options.RequireVisaOrRelocation));

        Assert.True(rig.Runner.Retry(chosen.Id, options: new RetryOptions(false, 0, RequireVisaOrRelocation: true)));
        Assert.True(rig.Store.Read(chosen.Id, r => r.Options.RequireVisaOrRelocation));
        Assert.Contains(rig.Store.Read(chosen.Id, r => r.Log.Select(l => l.Text).ToList()), line => line.Contains("only if it offers visa sponsorship or relocation"));
    }

    [Fact]
    public void Retry_when_available_saves_the_requirement_for_the_automatic_retry()
    {
        using var rig = new Rig();
        var job = rig.Limited(DateTime.UtcNow.AddMinutes(-10));

        Assert.True(rig.Runner.ArmAutoRetry(job.Id, new RetryOptions(false, 0, RequireVisaOrRelocation: true)));

        Assert.True(rig.Store.Read(job.Id, r => r.Options.RequireVisaOrRelocation));
    }

    [Fact]
    public void A_job_split_off_a_list_inherits_the_requirement()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var enqueued = new List<Guid>();
        var stage = new ExtractExecutor(new PipelineServices(null!, store, new ProfileService(env.Paths), null!, env.Paths, id => { enqueued.Add(id); return true; }));
        var parent = new JobRecord
        {
            ScreenshotFile = "screenshot.png", OriginalFileName = "list.png",
            Options = new JobOptions { AutoSubmit = false, MinMatchPercent = 40, RequireVisaOrRelocation = true }
        };
        Directory.CreateDirectory(env.Paths.JobFolder(parent.Id));
        File.WriteAllBytes(Path.Combine(env.Paths.JobFolder(parent.Id), parent.ScreenshotFile), new byte[] { 1 });
        store.Add(parent);

        stage.SplitOff(parent.Id, new[] { new ExtractedJob { Company = "Nextlane", Title = "Engineer" } }, 2);

        var child = Assert.Single(enqueued);
        Assert.True(store.Read(child, r => r.Options.RequireVisaOrRelocation));
        Assert.Equal(40, store.Read(child, r => r.Options.MinMatchPercent));
    }
}

public class VisaRelocationRescoreTests
{
    private static string MatchJson(string visa, string relocation, string evidence = "") => JsonSerializer.Serialize(new
    {
        SkillsScore = 80, ExperienceScore = 80, DomainScore = 80, SeniorityScore = 80, LogisticsScore = 80,
        Summary = "Good fit.", MatchedSkills = Array.Empty<string>(), MissingSkills = Array.Empty<string>(),
        VisaSponsorship = visa, Relocation = relocation, SupportEvidence = evidence
    });

    /// <summary>A pipeline whose agents run on a fake copilot that answers with <paramref name="answer"/>.</summary>
    private static (Rig Rig, PipelineServices Pipeline, string Cli, JobRecord Job) Setup(string answer, bool require, bool assessed)
    {
        var rig = new Rig();
        var cli = FakeCopilot.Install(rig.Env, new[] { FakeCopilot.Start(), FakeCopilot.Say(answer), FakeCopilot.Done() });
        rig.Settings.Save(new AppSettings { Provider = "copilot", CopilotPath = cli, AccountsDocumentPath = Path.Combine(rig.Env.Root, "a.docx") });
        var mcp = new JobMcpServer(rig.Store, rig.Broker, rig.Accounts, rig.Profile, rig.Settings);
        var factory = new AgentFactory(rig.Env.Paths, rig.Settings, rig.Store, mcp, new McpEndpointInfo());
        var pipeline = new PipelineServices(factory, rig.Store, rig.Profile, rig.Accounts, rig.Env.Paths);

        var profile = ProfileService.Clone(rig.Profile.Current);
        profile.ResumeText = "My resume";
        rig.Profile.Save(profile);

        var job = new JobRecord
        {
            Options = new JobOptions { RequireVisaOrRelocation = require },
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev", Description = "Build things." },
            Found = new FoundPosting { Found = true, Url = "https://acme.example/jobs/1", Description = new string('x', 300) + " We sponsor work visas for the right people." },
            // a score saved by an earlier version: good, but it was never asked about visas or relocation
            Match = assessed ? null : new MatchResult { SkillsScore = 90, ExperienceScore = 90, DomainScore = 90, SeniorityScore = 90, LogisticsScore = 90, SupportAssessed = false }
        };
        rig.Store.Add(job);
        return (rig, pipeline, cli, job);
    }

    private static Task<StageResult> Run(PipelineServices pipeline, JobRecord job) =>
        new MatchExecutor(pipeline).HandleAsync(new StageResult(job.Id, true), null!, CancellationToken.None).AsTask();

    [Fact]
    public async Task An_older_score_is_asked_for_again_when_the_requirement_is_on_and_the_answer_decides()
    {
        if (!FakeCopilot.Available) return;
        var (rig, pipeline, cli, job) = Setup(MatchJson("Offered", "NotMentioned", "We sponsor work visas for the right people."), require: true, assessed: false);
        using var _ = rig;

        var result = await Run(pipeline, job);

        Assert.True(FakeCopilot.WasRun(cli), "a score without an answer about visas must be made again");
        Assert.True(result.Continue);
        Assert.Equal((SupportLevel.Offered, true), rig.Store.Read(job.Id, r => (r.Match!.VisaSponsorship, r.Match.SupportAssessed)));
        Assert.Contains(rig.Store.Read(job.Id, r => r.Log.Select(l => l.Text).ToList()),
            line => line.StartsWith("Visa sponsorship: offered; relocation help: not mentioned") && line.Contains("We sponsor work visas"));
    }

    [Fact]
    public async Task A_posting_that_is_silent_is_skipped_after_the_new_score()
    {
        if (!FakeCopilot.Available) return;
        var (rig, pipeline, cli, job) = Setup(MatchJson("NotMentioned", "NotMentioned"), require: true, assessed: false);
        using var _ = rig;

        var result = await Run(pipeline, job);

        Assert.False(result.Continue);
        Assert.Equal(JobStatus.Skipped, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains("does not mention visa sponsorship", rig.Store.Read(job.Id, r => r.StatusDetail));
    }

    [Fact]
    public async Task The_matcher_is_told_what_counts_as_an_offer()
    {
        if (!FakeCopilot.Available) return;
        var (rig, pipeline, cli, job) = Setup(MatchJson("Offered", "Offered"), require: true, assessed: false);
        using var _ = rig;

        await Run(pipeline, job);

        var task = FakeCopilot.TaskSeen(cli);
        Assert.Contains("VisaSponsorship", task);
        Assert.Contains("relocation required", task);          // a requirement to move is not an offer of help
        Assert.Contains("We sponsor work visas for the right people.", task);   // the description it reads is the real posting's
        Assert.Contains("\"VisaSponsorship\"", task);            // and the result schema asks for the answers
    }

    [Fact]
    public async Task Without_the_requirement_an_older_score_is_kept_and_nothing_is_asked()
    {
        if (!FakeCopilot.Available) return;
        var (rig, pipeline, cli, job) = Setup(MatchJson("Offered", "Offered"), require: false, assessed: false);
        using var _ = rig;

        var result = await Run(pipeline, job);

        Assert.False(FakeCopilot.WasRun(cli));
        Assert.True(result.Continue);
        Assert.False(rig.Store.Read(job.Id, r => r.Match!.SupportAssessed));
    }

    [Fact]
    public async Task A_score_that_already_has_its_answers_is_not_asked_for_again()
    {
        if (!FakeCopilot.Available) return;
        var (rig, pipeline, cli, job) = Setup(MatchJson("NotMentioned", "NotMentioned"), require: true, assessed: false);
        using var _ = rig;
        rig.Store.Update(job.Id, r => r.Match!.SupportAssessed = true);
        rig.Store.Update(job.Id, r => r.Match!.Relocation = SupportLevel.Offered);

        var result = await Run(pipeline, job);

        Assert.False(FakeCopilot.WasRun(cli));
        Assert.True(result.Continue);
    }
}

public class VisaRelocationStorageTests
{
    [Fact]
    public void A_score_saved_before_the_answers_existed_reads_back_as_not_assessed_instead_of_failing()
    {
        using var env = new TestEnv();
        var job = new JobRecord
        {
            Options = new JobOptions { RequireVisaOrRelocation = true },
            Match = new MatchResult { SkillsScore = 70, ExperienceScore = 70, DomainScore = 70, SeniorityScore = 70, LogisticsScore = 70, Summary = "Old score." }
        };
        env.NewPersistence().Save(job);

        // What a database from before this option holds: the same JSON, without the four new properties.
        using (var db = env.Factory.CreateDbContext())
        {
            db.Database.ExecuteSqlRaw("UPDATE Jobs SET Match = json_remove(Match, '$.VisaSponsorship', '$.Relocation', '$.SupportEvidence', '$.SupportAssessed')");
            Assert.DoesNotContain("VisaSponsorship", db.Database.SqlQueryRaw<string>("SELECT Match AS Value FROM Jobs").AsEnumerable().Single());
        }

        var loaded = Assert.Single(env.NewPersistence().LoadAll());

        Assert.Equal("Old score.", loaded.Match!.Summary);
        Assert.Equal(70, loaded.Match.SkillsScore);
        Assert.False(loaded.Match.SupportAssessed);
        Assert.Equal(SupportLevel.NotMentioned, loaded.Match.VisaSponsorship);
        Assert.Equal(SupportLevel.NotMentioned, loaded.Match.Relocation);
    }

    [Fact]
    public void The_answers_are_stored_as_words_not_numbers()
    {
        using var env = new TestEnv();
        env.NewPersistence().Save(new JobRecord { Match = new MatchResult { VisaSponsorship = SupportLevel.NotOffered, Relocation = SupportLevel.Offered } });

        using var db = env.Factory.CreateDbContext();
        var json = db.Database.SqlQueryRaw<string>("SELECT Match AS Value FROM Jobs").AsEnumerable().Single();

        Assert.Contains("\"VisaSponsorship\":\"NotOffered\"", json.Replace(" ", ""));
        Assert.Contains("\"Relocation\":\"Offered\"", json.Replace(" ", ""));
    }
}

public class VisaRelocationControllerTests
{
    private static JobsController Controller(Rig rig) =>
        new(rig.Store, rig.Runner, rig.Broker, rig.Profile, rig.Settings, rig.Env.Paths)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public void The_dialogs_checkbox_reaches_the_job()
    {
        using var rig = new Rig();
        var job = new JobRecord { Status = JobStatus.Failed, Options = new JobOptions { AutoSubmit = false, MinMatchPercent = 10 } };
        rig.Store.Add(job);

        Controller(rig).Retry(job.Id, applyAnyway: false, optionsChosen: true, autoSubmit: false, minMatch: 10, requireVisaOrRelocation: true);

        Assert.True(rig.Store.Read(job.Id, r => r.Options.RequireVisaOrRelocation));
    }

    [Fact]
    public void Retry_all_failed_passes_the_choice_to_every_job()
    {
        using var rig = new Rig();
        var one = new JobRecord { Status = JobStatus.Failed };
        var two = new JobRecord { Status = JobStatus.Failed };
        rig.Store.Add(one);
        rig.Store.Add(two);

        Controller(rig).RetryAllFailed(optionsChosen: true, autoSubmit: false, minMatch: 0, requireVisaOrRelocation: true);

        Assert.All(new[] { one, two }, j => Assert.True(rig.Store.Read(j.Id, r => r.Options.RequireVisaOrRelocation)));
    }
}
