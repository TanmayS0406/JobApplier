using System.Text.Json;
using JobApplier.Claude;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Insights;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class ResumeCoachTests
{
    private static readonly CoachCandidate Kubernetes = new("Kubernetes", new[] { "Kubernetes", "K8s" }, 12, 5, false);
    private static readonly CoachCandidate Terraform = new("Terraform", new[] { "Terraform" }, 8, 2, false);
    private static readonly CoachCandidate[] Candidates = { Kubernetes, Terraform };

    private static StoredAdvice Clean(CoachAnswer answer, string? resume = null) =>
        ResumeCoach.Clean(answer, resume ?? InsightsData.Resume, Candidates, "fp", 70, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static CoachItem Item(string skill, string verdict, string wording = "", string evidence = "", string section = "Skills") =>
        new() { Skill = skill, Verdict = verdict, Wording = wording, Evidence = evidence, Section = section, Reason = "because" };

    [Fact]
    public void A_ShowIt_with_a_quote_that_is_in_the_resume_is_kept()
    {
        var advice = Clean(new CoachAnswer { Changes = { Item("Kubernetes", "ShowIt", "Deployed Docker services to Kubernetes", "building services in C# and .NET with SQL Server, Docker") } });

        var change = Assert.Single(advice.Changes);
        Assert.Equal(CoachVerdict.ShowIt, change.Verdict);
        Assert.Equal("Deployed Docker services to Kubernetes", change.Wording);
        Assert.Equal(5, change.NewlyRelevant);
        Assert.Equal(12, change.JobsMissingIt);
    }

    [Fact]
    public void A_quote_still_matches_when_the_resume_wraps_lines_or_changes_case()
    {
        var resume = "Seven years building services\r\n   in C# and .NET with SQL Server,\nDocker and REST APIs.";

        var advice = Clean(new CoachAnswer { Changes = { Item("Kubernetes", "showit", "Ran Docker in production", "BUILDING SERVICES IN C# and .NET with SQL Server, Docker") } }, resume);

        Assert.Equal(CoachVerdict.ShowIt, Assert.Single(advice.Changes).Verdict);
    }

    [Theory]
    [InlineData("I led a Kubernetes migration at Google")]   // invented
    [InlineData("")]
    [InlineData("Docker")]                                    // too short to prove anything
    public void A_ShowIt_without_proof_in_the_resume_becomes_LearnIt_and_loses_its_wording(string evidence)
    {
        var advice = Clean(new CoachAnswer { Changes = { Item("Kubernetes", "ShowIt", "Led the Kubernetes migration", evidence) } });

        var change = Assert.Single(advice.Changes);
        Assert.Equal(CoachVerdict.LearnIt, change.Verdict);
        Assert.Equal("", change.Wording);
        Assert.Equal("", change.Evidence);
        Assert.NotEqual("", change.Note);
    }

    [Fact]
    public void Wording_with_a_number_the_resume_does_not_contain_is_not_kept()
    {
        var advice = Clean(new CoachAnswer { Changes = { Item("Kubernetes", "ShowIt", "Cut deploy time by 80% with Kubernetes", "building services in C# and .NET") } });

        var change = Assert.Single(advice.Changes);
        Assert.Equal(CoachVerdict.LearnIt, change.Verdict);
        Assert.Contains("number", change.Note);
    }

    [Fact]
    public void A_number_from_the_resume_is_allowed_in_the_wording()
    {
        var advice = Clean(new CoachAnswer { Changes = { Item("Kubernetes", "ShowIt", "7 years of services, now on Kubernetes", "7 years building services") } }, "Backend engineer. 7 years building services in C#.");

        Assert.Equal(CoachVerdict.ShowIt, Assert.Single(advice.Changes).Verdict);
    }

    [Fact]
    public void A_suggestion_for_a_skill_that_was_not_offered_is_dropped_and_counted()
    {
        var advice = Clean(new CoachAnswer { Changes = { Item("Quantum computing", "ShowIt", "x", "Seven years building services"), Item("K8s", "LearnIt") } });

        Assert.Equal("Kubernetes", Assert.Single(advice.Changes).Skill);   // "K8s" is the candidate "Kubernetes"
        Assert.Equal(1, advice.Dropped);
    }

    [Fact]
    public void A_skill_is_listed_once_and_the_advice_is_ordered_ShowIt_then_LearnIt_then_Skip()
    {
        var advice = Clean(new CoachAnswer
        {
            Changes = { Item("Terraform", "Skip"), Item("Kubernetes", "LearnIt"), Item("kubernetes", "ShowIt", "x", "Seven years building services") }
        });

        Assert.Equal(new[] { CoachVerdict.LearnIt, CoachVerdict.Skip }, advice.Changes.Select(c => c.Verdict));
        Assert.Equal(new[] { 1, 2 }, advice.Changes.Select(c => c.Rank));
        Assert.Equal(1, advice.Dropped);
    }

    [Fact]
    public void An_unknown_verdict_means_skip_and_control_characters_are_removed()
    {
        var advice = Clean(new CoachAnswer { Summary = "Fine.\u0007\r\nReally.", Changes = { Item("Terraform", "maybe?") } });

        Assert.Equal(CoachVerdict.Skip, Assert.Single(advice.Changes).Verdict);
        Assert.DoesNotContain(advice.Summary, char.IsControl);
    }

    [Fact]
    public void The_prompt_holds_the_resume_as_data_and_the_candidates_and_cuts_a_very_long_resume()
    {
        var report = GapAnalyzer.Analyze(InsightsData.Resume, InsightsData.Corpus(), new AnalysisOptions(70));
        var candidates = ResumeCoach.Candidates(report);

        var prompt = ResumeCoach.BuildPrompt(InsightsData.Resume + new string('x', ResumeCoach.MaxResumeChars * 2), candidates, report);

        Assert.Contains("Kubernetes", prompt);
        Assert.Contains("RESUME (data)", prompt);
        Assert.True(prompt.Length < ResumeCoach.MaxResumeChars + 4000);
        Assert.InRange(candidates.Count, 1, ResumeCoach.MaxCandidates);
        Assert.Equal(candidates.Count, candidates.Select(c => SkillNormalizer.Key(c.Skill)).Distinct().Count());
    }
}

public class ResumeCoachServiceTests
{
    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class FakeRunner : ICoachRunner
    {
        public TaskCompletionSource<CoachAnswer> Answer { get; } = new();
        public int Calls;
        public string? Prompt;

        public Task<CoachAnswer> RunAsync(string prompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Prompt = prompt;
            return Answer.Task;
        }
    }

    private static (ResumeCoachService Service, FakeRunner Runner, GapAnalysisService Analysis, JobStore Jobs, ProfileService Profile, TestEnv Env) Create(bool withJobs = true, bool withResume = true)
    {
        var env = new TestEnv();
        var jobs = env.NewStore();
        var profile = new ProfileService(env.Paths);
        if (withResume) profile.Save(new UserProfile { ResumeText = InsightsData.Resume });
        if (withJobs)
            foreach (var j in InsightsData.Corpus())
                jobs.Add(new JobRecord
                {
                    Id = j.Id,
                    Extracted = new ExtractedJob { Company = j.Company, Title = j.Title, Description = j.Text },
                    Match = new MatchResult
                    {
                        SkillsScore = j.SkillsScore, ExperienceScore = j.ExperienceScore, DomainScore = j.DomainScore, SeniorityScore = j.SeniorityScore,
                        LogisticsScore = j.LogisticsScore, MatchedSkills = j.Matched.ToList(), MissingSkills = j.Missing.ToList()
                    }
                });
        var analysis = new GapAnalysisService(jobs, profile);
        var runner = new FakeRunner();
        return (new ResumeCoachService(env.Paths, analysis, runner, new StubLifetime(), NullLogger<ResumeCoachService>.Instance), runner, analysis, jobs, profile, env);
    }

    private static async Task<CoachStatus> Settled(ResumeCoachService service)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = service.Status(70);
            if (!status.Running) return status;
            await Task.Delay(25);
        }
        throw new TimeoutException("the coach did not finish");
    }

    [Fact]
    public async Task A_run_cleans_and_keeps_the_advice_and_a_second_start_while_running_is_refused()
    {
        var (service, runner, _, _, _, env) = Create();
        using var _ = env;

        var (started, _) = service.Start(70);
        Assert.True(started);
        Assert.True(service.Status(70).Running);
        Assert.False(service.Start(70).Started);          // one run at a time
        for (var i = 0; i < 200 && Volatile.Read(ref runner.Calls) == 0; i++) await Task.Delay(10);   // the run starts on a background task
        Assert.Equal(1, Volatile.Read(ref runner.Calls));
        Assert.Contains("Seven years building services", runner.Prompt);

        runner.Answer.SetResult(new CoachAnswer { Summary = "Good base.", Changes = { new CoachItem { Skill = "Kubernetes", Verdict = "LearnIt", Reason = "Nothing on the resume uses it." } } });
        var status = await Settled(service);

        Assert.Equal("", status.Error);
        Assert.Equal("Kubernetes", Assert.Single(status.Advice!.Changes).Skill);
        Assert.False(status.Stale);
        Assert.True(File.Exists(env.Paths.AdviceFile));
    }

    [Fact]
    public async Task The_advice_survives_a_restart_and_is_marked_stale_when_the_jobs_change()
    {
        var (service, runner, analysis, jobs, profile, env) = Create();
        using var _ = env;
        service.Start(70);
        runner.Answer.SetResult(new CoachAnswer { Summary = "ok", Changes = { new CoachItem { Skill = "Kubernetes", Verdict = "Skip" } } });
        await Settled(service);

        var restarted = new ResumeCoachService(env.Paths, analysis, new FakeRunner(), new StubLifetime(), NullLogger<ResumeCoachService>.Instance);
        Assert.Equal("ok", restarted.Status(70).Advice!.Summary);
        Assert.False(restarted.Status(70).Stale);

        profile.Save(new UserProfile { ResumeText = InsightsData.Resume + " Also built Helm charts." });
        Assert.True(restarted.Status(70).Stale);
        Assert.NotNull(jobs);
    }

    [Fact]
    public async Task A_failed_run_says_why_and_can_be_started_again()
    {
        var (service, runner, _, _, _, env) = Create();
        using var _ = env;
        service.Start(70);
        runner.Answer.SetException(new ClaudeAgentException("Not logged in", isAuthProblem: true));

        var status = await Settled(service);

        Assert.Contains("Not logged in", status.Error);
        Assert.Null(status.Advice);
        Assert.True(service.Start(70).Started);
    }

    [Fact]
    public async Task A_usage_limit_gets_a_plain_message()
    {
        var (service, runner, _, _, _, env) = Create();
        using var _ = env;
        service.Start(70);
        runner.Answer.SetException(new ClaudeAgentException("limit", isUsageLimit: true));

        Assert.Contains("usage limit", (await Settled(service)).Error);
    }

    [Fact]
    public void Nothing_starts_without_a_resume_or_without_scored_jobs_and_no_agent_is_asked()
    {
        var (noResume, runner1, _, _, _, env1) = Create(withResume: false);
        var (noJobs, runner2, _, _, _, env2) = Create(withJobs: false);
        using var _1 = env1;
        using var _2 = env2;

        Assert.Contains("resume", noResume.Start(70).Message);
        Assert.Contains("scored jobs", noJobs.Start(70).Message);
        Assert.Equal(0, runner1.Calls + runner2.Calls);
    }
}

public class InsightsControllerTests
{
    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class NeverRunner : ICoachRunner
    {
        public Task<CoachAnswer> RunAsync(string prompt, CancellationToken cancellationToken) => new TaskCompletionSource<CoachAnswer>().Task;
    }

    private static (InsightsController Controller, SettingsService Settings, TestEnv Env) Create(int defaultMin = 0)
    {
        var env = new TestEnv();
        var jobs = env.NewStore();
        var profile = new ProfileService(env.Paths);
        profile.Save(new UserProfile { ResumeText = InsightsData.Resume });
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "a.docx"), DefaultMinMatchPercent = defaultMin });
        var analysis = new GapAnalysisService(jobs, profile);
        var coach = new ResumeCoachService(env.Paths, analysis, new NeverRunner(), new StubLifetime(), NullLogger<ResumeCoachService>.Instance);
        return (new InsightsController(analysis, coach, settings), settings, env);
    }

    [Theory]
    [InlineData(null, 0, 70)]      // no setting: 70
    [InlineData(null, 66, 65)]     // the dashboard's minimum, moved to the nearest threshold offered
    [InlineData(null, 100, 80)]
    [InlineData(55, 0, 60)]
    [InlineData(76, 0, 75)]
    [InlineData(80, 0, 80)]
    public void The_threshold_is_the_one_asked_for_or_the_dashboards_minimum_moved_to_one_the_page_offers(int? asked, int defaultMin, int expected)
    {
        var (controller, _, env) = Create(defaultMin);
        using var _ = env;

        var model = Assert.IsType<InsightsViewModel>(Assert.IsType<ViewResult>(controller.Index(asked)).Model);

        Assert.Equal(expected, model.Threshold);
        Assert.Equal(expected, model.Report.Threshold);
    }

    [Fact]
    public void With_no_jobs_the_page_still_opens_and_starting_the_coach_says_why_not()
    {
        var (controller, _, env) = Create();
        using var _ = env;

        var model = Assert.IsType<InsightsViewModel>(Assert.IsType<ViewResult>(controller.Index(null)).Model);
        Assert.False(model.Report.HasData);

        var json = JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(controller.StartCoach(null)).Value);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("scored jobs", json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/Views/Insights/Index.cshtml")]
    [InlineData("/Views/Insights/_Coach.cshtml")]
    public void The_insights_views_are_compiled(string identifier)
    {
        var compiled = typeof(InsightsController).Assembly
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemAttribute>()
            .Select(a => a.Identifier);

        Assert.Contains(identifier, compiled);
    }
}
