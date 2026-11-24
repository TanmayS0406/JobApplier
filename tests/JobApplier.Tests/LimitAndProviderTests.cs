using System.Text.Json.Nodes;
using JobApplier.Agents;
using JobApplier.Claude;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Mcp;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class UsageLimitDetectionTests
{
    [Theory]
    [InlineData("Claude AI usage limit reached|1767225600")]
    [InlineData("5-hour limit reached ∙ resets 3pm")]
    [InlineData("You've reached your weekly limit. It resets Mon at 9am.")]
    [InlineData("You've hit your limit · resets 3pm (Asia/Calcutta)")]
    [InlineData("API Error: 429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}")]
    [InlineData("Error 429: RESOURCE_EXHAUSTED. You exceeded your current quota, please check your plan.")]
    [InlineData("You have exhausted your capacity on this model. Your quota will reset after 3h45m10s.")]
    [InlineData("Too Many Requests")]
    public void Recognises_the_wording_both_CLIs_use(string text) => Assert.True(UsageLimit.IsLimit(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Claude Code exited with code 1 without a result.")]
    [InlineData("Not logged in. Please run /login")]
    [InlineData("The posting was closed (HTTP 404).")]
    [InlineData("Call +91 429 555 0100 for support")]
    public void Ordinary_failures_are_not_limits(string? text) => Assert.False(UsageLimit.IsLimit(text));
}

public class UsageLimitResetTests
{
    // Saturday 14 November 2026, 12:00 UTC.
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_epoch_after_the_message_is_the_reset_time()
    {
        var reset = Now.AddHours(2);
        var epoch = new DateTimeOffset(reset).ToUnixTimeSeconds();

        Assert.Equal(reset, UsageLimit.ResetTime($"Claude AI usage limit reached|{epoch}", Now));
    }

    [Theory]
    [InlineData("quota will reset after 3h45m10s", 3 * 3600 + 45 * 60 + 10 + 1)]
    [InlineData("Please retry in 34.07s", 36)]
    [InlineData("usage limit; try again in 5 minutes", 5 * 60 + 1)]
    [InlineData("rate limit: resets in 2 hours 10 minutes", 2 * 3600 + 10 * 60 + 1)]
    public void A_wait_is_added_to_now(string text, int seconds) =>
        Assert.Equal(Now.AddSeconds(seconds), UsageLimit.ResetTime(text, Now));

    [Theory]
    [InlineData("5-hour limit reached ∙ resets 3pm (UTC)", 2026, 11, 14, 15, 0)]    // later today
    [InlineData("limit reached, resets 9am (UTC)", 2026, 11, 15, 9, 0)]              // already past today: tomorrow
    [InlineData("limit reached, resets 3:30 pm (UTC)", 2026, 11, 14, 15, 30)]
    [InlineData("weekly limit. It resets Mon at 9am (UTC)", 2026, 11, 16, 9, 0)]     // next Monday
    [InlineData("weekly limit resets Nov 20 at 3pm (UTC)", 2026, 11, 20, 15, 0)]
    [InlineData("limit reached - resets 18:45 (UTC)", 2026, 11, 14, 18, 45)]
    public void A_clock_time_is_the_next_time_it_occurs(string text, int y, int mo, int d, int h, int mi) =>
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc), UsageLimit.ResetTime(text, Now));

    [Fact]
    public void A_stated_time_zone_is_honoured()
    {
        // 3pm in India (UTC+5:30) is 09:30 UTC, which is before 12:00 UTC today, so it is tomorrow's.
        var reset = UsageLimit.ResetTime("5-hour limit reached ∙ resets 3pm (Asia/Calcutta)", Now);

        Assert.Equal(new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc), reset);
    }

    [Theory]
    [InlineData("usage limit reached")]
    [InlineData("resets sometime")]
    [InlineData("usage limit reached|1000000000")]                  // an epoch in the past
    [InlineData("limit reached, resets in 40 days")]               // implausibly far: not trusted
    [InlineData("limit reached, resets 2026-12-01")]               // a date, not a time of day
    public void Text_without_a_usable_time_gives_null(string text) => Assert.Null(UsageLimit.ResetTime(text, Now));

    [Fact]
    public void Backoff_grows_then_levels_off()
    {
        Assert.Equal(new[] { 3, 10, 20, 30, 60, 60 }, Enumerable.Range(0, 6).Select(i => (int)UsageLimit.Backoff(i).TotalMinutes));
    }

    [Fact]
    public void A_description_names_the_time_and_how_long_it_is()
    {
        Assert.Contains("in 30 min", UsageLimit.Describe(Now.AddMinutes(30), Now));
        Assert.Contains("in 3 h 5 min", UsageLimit.Describe(Now.AddMinutes(185), Now));
        Assert.Contains("now", UsageLimit.Describe(Now.AddMinutes(-1), Now));
    }
}

public class AgentErrorTests
{
    [Fact]
    public void A_limit_is_flagged_with_its_reset_time()
    {
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        var ex = AgentErrors.Describe("quota will reset after 1h", "sign in", now);

        Assert.True(ex.IsUsageLimit);
        Assert.False(ex.IsAuthProblem);
        Assert.Equal(now.AddSeconds(3601), ex.RetryAfterUtc);
    }

    [Theory]
    [InlineData("Not logged in. Please run /login", "Open Settings and press 'Sign in with Claude'.")]
    [InlineData("Error: No authentication information found", "Open Settings and press 'Sign in with GitHub'.")]
    [InlineData("GitHub credential authority error: GitHub CLI could not provide a token", "Open Settings and press 'Sign in with GitHub'.")]
    public void A_sign_in_problem_gets_the_providers_hint(string error, string hint)
    {
        var ex = AgentErrors.Describe(error, hint);

        Assert.True(ex.IsAuthProblem);
        Assert.False(ex.IsUsageLimit);
        Assert.EndsWith(hint, ex.Message);
    }

    [Fact]
    public void Anything_else_is_passed_through_unchanged()
    {
        var ex = AgentErrors.Describe("The page crashed.", "hint");

        Assert.False(ex.IsAuthProblem);
        Assert.False(ex.IsUsageLimit);
        Assert.Equal("The page crashed.", ex.Message);
    }
}

public class JsonExtractTests
{
    [Theory]
    [InlineData("""Here you go: {"a":1} thanks""", """{"a":1}""")]
    [InlineData("```json\n{\"a\":{\"b\":[1,2]}}\n```", """{"a":{"b":[1,2]}}""")]
    [InlineData("""{"text":"a } brace and \"quote\" inside"}""", """{"text":"a } brace and \"quote\" inside"}""")]
    [InlineData("""{not json} then {"ok":true}""", """{"ok":true}""")]
    public void The_object_is_found_inside_the_answer(string text, string expected) => Assert.Equal(expected, JsonExtract.FirstObject(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no object here")]
    [InlineData("{ unterminated")]
    public void No_object_gives_null(string? text) => Assert.Null(JsonExtract.FirstObject(text));
}

public class ProviderSettingsTests
{
    [Theory]
    [InlineData("copilot", "copilot")]
    [InlineData(" COPILOT ", "copilot")]
    [InlineData("claude", "claude")]
    [InlineData("", "claude")]
    [InlineData(null, "claude")]
    [InlineData("gpt", "claude")]
    [InlineData("gemini", "claude")]   // a setting saved by an earlier version, which supported Gemini, still starts the app
    public void Anything_but_copilot_means_Claude(string? input, string expected) => Assert.Equal(expected, AiProvider.Normalize(input));

    [Fact]
    public void The_provider_is_saved_and_a_missing_Copilot_path_is_rejected()
    {
        using var env = new TestEnv();
        var service = new SettingsService(env.Paths);

        Assert.Null(service.Save(new AppSettings { Provider = "Copilot", CopilotModel = " auto " }));
        Assert.Equal("copilot", service.Current.Provider);
        Assert.Equal("auto", service.Current.CopilotModel);

        Assert.Contains("Copilot CLI path", service.Save(new AppSettings { Provider = "copilot", CopilotPath = Path.Combine(env.Root, "nope.cmd") }));
        Assert.Equal("copilot", new SettingsService(env.Paths).Current.Provider); // the earlier, valid save is what is stored
    }

    [Fact]
    public void Settings_saved_by_the_version_that_supported_Gemini_still_load_and_run_on_Claude()
    {
        using var env = new TestEnv();
        File.WriteAllText(env.Paths.SettingsFile, """{"Provider":"gemini","GeminiPath":"C:/old/gemini.cmd","GeminiModel":"gemini-2.5-pro","Model":"opus","MaxParallelJobs":4}""");

        var settings = new SettingsService(env.Paths).Current;

        Assert.Equal("claude", settings.Provider);
        Assert.Equal("opus", settings.Model);
        Assert.Equal(4, settings.MaxParallelJobs);
    }

    [Fact]
    public void The_factory_builds_the_agent_of_the_selected_provider()
    {
        using var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        var store = env.NewStore();
        var profile = new ProfileService(env.Paths);
        var accounts = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
        var mcp = new JobMcpServer(store, new HumanHelpBroker(store), accounts, profile, settings);
        var factory = new AgentFactory(env.Paths, settings, store, mcp, new McpEndpointInfo());
        string Folder(string n) => Path.Combine(env.Root, n);

        settings.Save(new AppSettings { Provider = "claude" });
        Assert.IsType<ClaudeCodeAgent>(factory.CreateStandalone("r", "i", Folder("w1"), Folder("p1")));

        settings.Save(new AppSettings { Provider = "copilot" });
        Assert.IsType<CopilotCliAgent>(factory.CreateStandalone("r", "i", Folder("w2"), Folder("p2")));
    }
}

/// <summary>A job runner and the services around it, with the pipeline's limit notifications recorded.</summary>
internal sealed class Rig : IDisposable
{
    private readonly bool _ownsEnv;

    public TestEnv Env { get; }
    public JobStore Store { get; }
    public SettingsService Settings { get; }
    public ProfileService Profile { get; }
    public AccountService Accounts { get; }
    public HumanHelpBroker Broker { get; }
    public PipelineServices Pipeline { get; }
    public JobRunner Runner { get; }
    public RecordingJobQueue Queue { get; } = new();
    public List<DateTime?> LimitNotices { get; } = new();

    public Rig() : this(new TestEnv(), ownsEnv: true) { }

    /// <summary>
    /// The app started again over the same data folder and database: everything is new, so only what an earlier rig flushed is there.
    /// The earlier rig still owns (and removes) the folder.
    /// </summary>
    public Rig(TestEnv sameData) : this(sameData, ownsEnv: false) { }

    private Rig(TestEnv env, bool ownsEnv)
    {
        Env = env;
        _ownsEnv = ownsEnv;
        Settings = new SettingsService(Env.Paths);
        Settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(Env.Root, "a.docx") });
        Store = Env.NewStore();
        Profile = new ProfileService(Env.Paths);
        Accounts = new AccountService(Env.Paths, Settings, NullLogger<AccountService>.Instance);
        Broker = new HumanHelpBroker(Store);
        Pipeline = new PipelineServices(null!, Store, Profile, Accounts, Env.Paths, null, until => LimitNotices.Add(until));
        Runner = new JobRunner(Store, Broker, Settings, Pipeline, Queue, NullLogger<JobRunner>.Instance);
    }

    /// <summary>A failed job that stopped on a usage limit.</summary>
    public JobRecord Limited(DateTime? retryAfter, bool auto = false, int retries = 0, DateTime? updated = null)
    {
        var job = new JobRecord
        {
            Status = JobStatus.Failed,
            LimitHit = true,
            RetryAfterUtc = retryAfter,
            AutoRetry = auto,
            LimitRetries = retries,
            UpdatedUtc = updated ?? DateTime.UtcNow,
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" }
        };
        Store.Add(job);
        return job;
    }

    public void Dispose()
    {
        if (_ownsEnv) Env.Dispose();
    }
}

public class UsageLimitRetryTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void Only_a_failed_limit_hit_job_can_be_armed()
    {
        using var rig = new Rig();
        var limited = rig.Limited(Now.AddHours(1));
        var plain = new JobRecord { Status = JobStatus.Failed };
        var running = new JobRecord { Status = JobStatus.Applying, LimitHit = true };
        rig.Store.Add(plain);
        rig.Store.Add(running);

        Assert.True(rig.Runner.ArmAutoRetry(limited.Id));
        Assert.False(rig.Runner.ArmAutoRetry(limited.Id));   // already armed
        Assert.False(rig.Runner.ArmAutoRetry(plain.Id));
        Assert.False(rig.Runner.ArmAutoRetry(running.Id));
        Assert.True(rig.Store.Read(limited.Id, r => r.WaitingForLimitReset));
    }

    [Fact]
    public void Arming_every_limited_job_counts_them()
    {
        using var rig = new Rig();
        rig.Limited(Now.AddHours(1));
        rig.Limited(Now.AddHours(2));
        rig.Limited(Now.AddHours(2), auto: true);
        rig.Store.Add(new JobRecord { Status = JobStatus.Failed });

        Assert.Equal(2, rig.Runner.ArmAllLimited());
        Assert.Equal(0, rig.Runner.ArmAllLimited());
    }

    [Fact]
    public void A_job_is_retried_once_its_reset_time_has_passed_and_not_before()
    {
        using var rig = new Rig();
        var later = rig.Limited(Now.AddHours(2), auto: true);
        var due = rig.Limited(Now.AddMinutes(-10), auto: true);

        var started = rig.Runner.RetryDueJobs(Now);

        Assert.Equal(1, started);
        Assert.Equal(JobStatus.Queued, rig.Store.Read(due.Id, r => r.Status));
        Assert.Equal(JobStatus.Failed, rig.Store.Read(later.Id, r => r.Status));
        Assert.Contains(rig.Store.Read(due.Id, r => r.Log.Select(l => l.Text).ToList())!, t => t.Contains("Retrying automatically"));
    }

    [Fact]
    public void A_job_that_was_not_armed_is_left_alone_however_late_it_is()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddDays(-1), auto: false);

        Assert.Equal(0, rig.Runner.RetryDueJobs(Now));
        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public void An_automatic_retry_keeps_waiting_in_case_the_limit_is_still_in_force()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddMinutes(-10), auto: true, retries: 2);

        rig.Runner.RetryDueJobs(Now);

        Assert.True(rig.Store.Read(job.Id, r => r.AutoRetry));
        Assert.Equal(3, rig.Store.Read(job.Id, r => r.LimitRetries));
        Assert.False(rig.Store.Read(job.Id, r => r.LimitHit));    // cleared until it fails the same way again
    }

    [Fact]
    public void Without_a_stated_reset_the_wait_grows_with_each_attempt()
    {
        using var rig = new Rig();
        var first = rig.Limited(null, auto: true, retries: 0, updated: Now.AddMinutes(-5));    // 3 min back-off: over
        var third = rig.Limited(null, auto: true, retries: 2, updated: Now.AddMinutes(-5));    // 20 min back-off: not yet

        rig.Runner.RetryDueJobs(Now);

        Assert.Equal(JobStatus.Queued, rig.Store.Read(first.Id, r => r.Status));
        Assert.Equal(JobStatus.Failed, rig.Store.Read(third.Id, r => r.Status));
    }

    [Fact]
    public void After_too_many_attempts_it_gives_up_and_leaves_the_job_for_the_user()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddMinutes(-10), auto: true, retries: JobRunner.MaxAutoRetries);

        Assert.Equal(0, rig.Runner.RetryDueJobs(Now));

        Assert.False(rig.Store.Read(job.Id, r => r.AutoRetry));
        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains(rig.Store.Read(job.Id, r => r.Log.Select(l => l.Text).ToList())!, t => t.Contains("Gave up"));
    }

    [Fact]
    public void While_new_jobs_are_held_back_nothing_is_retried()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddMinutes(-10), auto: true);
        rig.Runner.NoteUsageLimit(Now.AddMinutes(30));

        Assert.NotNull(rig.Runner.PausedUntilUtc);
        Assert.Equal(0, rig.Runner.RetryDueJobs(Now));
        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public void The_hold_lasts_until_the_reset_plus_a_margin_and_a_shorter_limit_never_shortens_it()
    {
        using var rig = new Rig();

        rig.Runner.NoteUsageLimit(Now.AddHours(2));
        var longer = rig.Runner.PausedUntilUtc!.Value;
        rig.Runner.NoteUsageLimit(Now.AddMinutes(5));

        Assert.True(longer > Now.AddHours(2));
        Assert.True(longer < Now.AddHours(2).AddMinutes(2));
        Assert.Equal(longer, rig.Runner.PausedUntilUtc);
    }

    [Fact]
    public void An_unknown_reset_holds_new_jobs_back_for_a_short_while()
    {
        using var rig = new Rig();

        rig.Runner.NoteUsageLimit(null);

        Assert.InRange((rig.Runner.PausedUntilUtc!.Value - DateTime.UtcNow).TotalMinutes, 3, 5);
    }

    [Fact]
    public void A_retry_the_user_makes_cancels_the_wait()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddHours(1), auto: true, retries: 4);

        Assert.True(rig.Runner.Retry(job.Id));

        Assert.False(rig.Store.Read(job.Id, r => r.AutoRetry));
        Assert.Equal(0, rig.Store.Read(job.Id, r => r.LimitRetries));
        Assert.False(rig.Store.Read(job.Id, r => r.LimitHit));
    }

    [Fact]
    public void Stopping_the_wait_leaves_the_job_failed()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddHours(1), auto: true);

        Assert.True(rig.Runner.DisarmAutoRetry(job.Id));
        Assert.False(rig.Runner.DisarmAutoRetry(job.Id));

        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.True(rig.Store.Read(job.Id, r => r.LimitHit));
    }

    [Fact]
    public void A_waiting_job_and_its_state_survive_a_restart()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddHours(1), auto: true, retries: 1);
        rig.Store.Flush();

        var reloaded = rig.Env.NewStore();

        Assert.True(reloaded.Read(job.Id, r => r.WaitingForLimitReset));
        Assert.Equal(1, reloaded.Read(job.Id, r => r.LimitRetries));
        Assert.NotNull(reloaded.Read(job.Id, r => r.RetryAfterUtc));
    }
}

public class StageLimitHandlingTests
{
    /// <summary>A stage that fails the way a run does when the CLI reports a limit.</summary>
    private sealed class ThrowingStage : StageExecutor
    {
        private readonly Exception _error;
        public ThrowingStage(string id, PipelineServices services, Exception error) : base(id, services) => _error = error;
        protected override Task<bool> RunStageAsync(Guid jobId, CancellationToken cancellationToken) => throw _error;
    }

    private static JobRecord Running(Rig rig)
    {
        var job = new JobRecord { Status = JobStatus.Searching, Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" } };
        rig.Store.Add(job);
        return job;
    }

    [Fact]
    public async Task A_limit_marks_the_job_as_waiting_for_the_reset_and_holds_the_queue_back()
    {
        using var rig = new Rig();
        var job = Running(rig);
        var reset = DateTime.UtcNow.AddHours(2);
        var stage = new ThrowingStage("find", rig.Pipeline, new ClaudeAgentException("5-hour limit reached", isUsageLimit: true, retryAfterUtc: reset));

        var result = await stage.HandleAsync(new StageResult(job.Id, true), null!);

        Assert.False(result.Continue);
        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.True(rig.Store.Read(job.Id, r => r.LimitHit));
        Assert.True(rig.Store.Read(job.Id, r => r.LimitResetKnown));
        Assert.Equal(reset, rig.Store.Read(job.Id, r => r.RetryAfterUtc));
        Assert.Contains("usage limit", rig.Store.Read(job.Id, r => r.StatusDetail)!);
        Assert.Equal(reset, Assert.Single(rig.LimitNotices));
    }

    [Fact]
    public async Task Without_a_reset_time_the_next_try_is_estimated()
    {
        using var rig = new Rig();
        var job = Running(rig);
        var stage = new ThrowingStage("find", rig.Pipeline, new ClaudeAgentException("rate limit", isUsageLimit: true));

        await stage.HandleAsync(new StageResult(job.Id, true), null!);

        Assert.False(rig.Store.Read(job.Id, r => r.LimitResetKnown));
        Assert.InRange((rig.Store.Read(job.Id, r => r.RetryAfterUtc)!.Value - DateTime.UtcNow).TotalMinutes, 2.5, 3.5);
        Assert.Contains("No reset time was given", rig.Store.Read(job.Id, r => r.StatusDetail)!);
    }

    [Fact]
    public async Task A_limit_during_the_apply_step_warns_that_the_form_may_already_have_gone_through()
    {
        using var rig = new Rig();
        var job = Running(rig);
        var stage = new ThrowingStage("apply", rig.Pipeline, new ClaudeAgentException("limit reached", isUsageLimit: true));

        await stage.HandleAsync(new StageResult(job.Id, true), null!);

        Assert.Contains("check the employer's site", rig.Store.Read(job.Id, r => r.StatusDetail)!);
    }

    [Fact]
    public async Task Other_failures_are_not_treated_as_limits_and_clear_an_earlier_one()
    {
        using var rig = new Rig();
        var job = Running(rig);
        rig.Store.Update(job.Id, r => { r.LimitHit = true; r.RetryAfterUtc = DateTime.UtcNow.AddHours(1); });
        var stage = new ThrowingStage("find", rig.Pipeline, new ClaudeAgentException("The page crashed."));

        await stage.HandleAsync(new StageResult(job.Id, true), null!);

        Assert.False(rig.Store.Read(job.Id, r => r.LimitHit));
        Assert.Null(rig.Store.Read(job.Id, r => r.RetryAfterUtc));
        Assert.Empty(rig.LimitNotices);
    }
}

public class LimitControllerTests
{
    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static JobsController Controller(Rig rig)
    {
        var http = new DefaultHttpContext();
        return new JobsController(rig.Store, rig.Runner, rig.Broker, rig.Profile, rig.Settings, rig.Env.Paths)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempData())
        };
    }

    [Fact]
    public void Retry_when_available_arms_the_job_and_reports_it()
    {
        using var rig = new Rig();
        var job = rig.Limited(DateTime.UtcNow.AddHours(1));
        var controller = Controller(rig);

        controller.RetryWhenAvailable(job.Id);

        Assert.True(rig.Store.Read(job.Id, r => r.AutoRetry));
        Assert.Contains("retried automatically", (string)controller.TempData["Message"]!);
    }

    [Fact]
    public void Retry_when_available_refuses_a_job_that_did_not_hit_a_limit()
    {
        using var rig = new Rig();
        var job = new JobRecord { Status = JobStatus.Failed };
        rig.Store.Add(job);
        var controller = Controller(rig);

        controller.RetryWhenAvailable(job.Id);

        Assert.False(rig.Store.Read(job.Id, r => r.AutoRetry));
        Assert.NotNull(controller.TempData["Error"]);
    }

    [Fact]
    public void The_dashboard_button_arms_every_limited_job()
    {
        using var rig = new Rig();
        var a = rig.Limited(DateTime.UtcNow.AddHours(1));
        var b = rig.Limited(null);
        var controller = Controller(rig);

        controller.RetryAllWhenAvailable();

        Assert.True(rig.Store.Read(a.Id, r => r.AutoRetry));
        Assert.True(rig.Store.Read(b.Id, r => r.AutoRetry));
        Assert.Contains("2 jobs", (string)controller.TempData["Message"]!);
    }

    [Fact]
    public void Clear_failed_never_deletes_a_job_that_is_waiting_for_a_limit_to_reset()
    {
        using var rig = new Rig();
        var waiting = rig.Limited(DateTime.UtcNow.AddHours(1), auto: true);
        var limitedButNotArmed = rig.Limited(DateTime.UtcNow.AddHours(1), auto: false);
        var plain = new JobRecord { Status = JobStatus.Failed };
        rig.Store.Add(plain);
        var controller = Controller(rig);

        controller.ClearFailed();

        Assert.True(rig.Store.Exists(waiting.Id));
        Assert.False(rig.Store.Exists(limitedButNotArmed.Id));
        Assert.False(rig.Store.Exists(plain.Id));
        Assert.Contains("Cleared 2", (string)controller.TempData["Message"]!);
    }

    [Fact]
    public void The_dashboard_rows_carry_the_limit_state()
    {
        using var rig = new Rig();
        var reset = DateTime.UtcNow.AddHours(1);
        var job = rig.Limited(reset, auto: true);

        var row = LiveViewModelBuilder.Build(rig.Store, reset).Rows.Single(r => r.Id == job.Id);

        Assert.True(row.LimitHit);
        Assert.True(row.AutoRetry);
        Assert.Equal(reset, row.RetryAfterUtc);
        Assert.Equal(reset, LiveViewModelBuilder.Build(rig.Store, reset).PausedUntilUtc);
    }
}
