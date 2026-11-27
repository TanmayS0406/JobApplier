using JobApplier.Domain;
using JobApplier.Models;
using JobApplier.Services;

namespace JobApplier.Tests;

/// <summary>"Pause when the usage limit ends": jobs wait for the user's Resume instead of starting by themselves.</summary>
public class PauseAfterLimitTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static void SetPause(Rig rig, bool pause) =>
        Assert.Null(rig.Settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(rig.Env.Root, "a.docx"), PauseAfterLimit = pause }));

    [Fact]
    public void By_default_jobs_resume_by_themselves_once_the_limit_has_reset()
    {
        using var rig = new Rig();
        var job = rig.Limited(Now.AddMinutes(-10), auto: true);
        rig.Runner.NoteUsageLimit(Now.AddMinutes(-5));

        Assert.False(rig.Runner.HeldByUser);
        Assert.Equal(1, rig.Runner.RetryDueJobs(Now.AddHours(1)));
        Assert.Equal(JobStatus.Queued, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public void With_the_pause_on_nothing_resumes_after_the_reset_until_the_user_presses_Resume()
    {
        using var rig = new Rig();
        SetPause(rig, true);
        var job = rig.Limited(Now.AddMinutes(-10), auto: true);
        rig.Runner.NoteUsageLimit(Now.AddMinutes(-5));

        Assert.True(rig.Runner.HeldByUser);
        Assert.Equal(0, rig.Runner.RetryDueJobs(Now.AddHours(5)));   // long after the reset
        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.True(rig.Store.Read(job.Id, r => r.AutoRetry));        // still armed, just not started

        rig.Runner.ResumeAfterLimit();

        Assert.False(rig.Runner.HeldByUser);
        Assert.Equal(1, rig.Runner.RetryDueJobs(Now.AddHours(5)));
        Assert.Equal(JobStatus.Queued, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public void Turning_the_pause_off_lifts_the_hold_without_pressing_Resume()
    {
        using var rig = new Rig();
        SetPause(rig, true);
        rig.Runner.NoteUsageLimit(Now.AddMinutes(-5));
        Assert.True(rig.Runner.HeldByUser);

        SetPause(rig, false);

        Assert.False(rig.Runner.HeldByUser);
    }

    [Fact]
    public void The_pause_holds_nothing_back_when_no_usage_limit_was_ever_hit()
    {
        using var rig = new Rig();
        SetPause(rig, true);

        Assert.False(rig.Runner.HeldByUser);
    }

    [Fact]
    public void After_a_restart_jobs_that_were_waiting_for_a_limit_stay_held()
    {
        using var rig = new Rig();
        SetPause(rig, true);
        rig.Limited(Now.AddMinutes(-30), auto: true);   // the limit was hit before the app was closed, and has since reset

        rig.Runner.RecoverAfterRestart();

        Assert.True(rig.Runner.HeldByUser);
        Assert.Equal(0, rig.Runner.RetryDueJobs(Now));
    }

    [Fact]
    public void The_dashboard_knows_when_jobs_are_held()
    {
        using var rig = new Rig();

        Assert.True(LiveViewModelBuilder.Build(rig.Store, null, null, null, heldByUser: true).HeldByUser);
        Assert.False(LiveViewModelBuilder.Build(rig.Store).HeldByUser);
    }
}

public class ModelChoicesTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("copilot")]
    public void Every_list_starts_with_the_default_and_has_no_value_twice(string which)
    {
        var list = which == "claude" ? ModelChoices.Claude : ModelChoices.Copilot;

        Assert.Equal("", list[0].Value);                                    // the CLI's own default
        Assert.Equal(list.Count, list.Select(c => c.Value).Distinct().Count());
        Assert.DoesNotContain(list, c => c.Value == "__other" || c.Label.Length == 0);
    }

    [Fact]
    public void The_Claude_list_offers_the_aliases_and_the_full_model_ids()
    {
        var values = ModelChoices.Claude.Select(c => c.Value).ToList();

        Assert.Contains("haiku", values);
        Assert.Contains("sonnet", values);
        Assert.Contains("opus", values);
        Assert.Contains("claude-sonnet-5-5", values);
        Assert.Contains("auto", ModelChoices.Copilot.Select(c => c.Value));
    }

    [Fact]
    public void Pausing_after_a_limit_is_off_unless_asked_for() => Assert.False(new AppSettings().PauseAfterLimit);
}
