using Hangfire;
using Hangfire.Common;
using JobApplier.Persistence;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class EfJobPersistenceTests
{
    private static JobRecord Everything()
    {
        var job = new JobRecord
        {
            CreatedUtc = new DateTime(2026, 11, 1, 8, 30, 15, 123, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
            ScreenshotFile = "screenshot.png",
            OriginalFileName = "Zürich – Entwickler (1).png",
            Status = JobStatus.Applying,
            StatusDetail = "Filling in the application",
            Options = new JobOptions { AutoSubmit = true, MinMatchPercent = 70, RequireVisaOrRelocation = true },
            QueueId = "17",
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev", Skills = { "C#", "SQL" }, Description = "Build things", WorkMode = "Remote" },
            Found = new FoundPosting { Found = true, Url = "https://acme.example/jobs/1", Portal = "Greenhouse", Confidence = "high" },
            Match = new MatchResult
            {
                SkillsScore = 80, ExperienceScore = 70, DomainScore = 60, SeniorityScore = 90, LogisticsScore = 100,
                Summary = "Good fit", MatchedSkills = { "C#" }, MissingSkills = { "Go" },
                VisaSponsorship = SupportLevel.Offered, Relocation = SupportLevel.NotOffered,
                SupportEvidence = "We sponsor visas; no relocation help.", SupportAssessed = true
            },
            Apply = new ApplyOutcome { Result = ApplyResultKind.AlreadyApplied, Summary = "Seen before", Confirmation = "ref-9" },
            PendingHelp = new HelpRequest { Id = "ab12cd34", Kind = HelpKind.Captcha, Message = "Solve it", CreatedUtc = new DateTime(2026, 11, 1, 8, 59, 0, DateTimeKind.Utc) },
            AccountRecorded = true,
            LimitHit = true,
            RetryAfterUtc = new DateTime(2026, 11, 1, 13, 0, 0, DateTimeKind.Utc),
            LimitResetKnown = true,
            AutoRetry = true,
            LimitRetries = 3,
            Password = "never-stored"
        };
        job.Log.Add(new LogEntry { Utc = new DateTime(2026, 11, 1, 8, 31, 0, DateTimeKind.Utc), Kind = "info", Text = "Started." });
        job.Log.Add(new LogEntry { Utc = new DateTime(2026, 11, 1, 8, 32, 0, DateTimeKind.Utc), Kind = "warn", Text = "Usage limit reached: \"5-hour limit\"" });
        return job;
    }

    [Fact]
    public void A_job_with_everything_on_it_comes_back_the_same()
    {
        using var env = new TestEnv();
        var original = Everything();
        env.NewPersistence().Save(original);

        var job = Assert.Single(env.NewPersistence().LoadAll());

        Assert.Equal(original.Id, job.Id);
        Assert.Equal(original.CreatedUtc, job.CreatedUtc);
        Assert.Equal(original.UpdatedUtc, job.UpdatedUtc);
        Assert.Equal("Zürich – Entwickler (1).png", job.OriginalFileName);
        Assert.Equal(JobStatus.Applying, job.Status);
        Assert.Equal("Filling in the application", job.StatusDetail);
        Assert.True(job.Options.AutoSubmit);
        Assert.Equal(70, job.Options.MinMatchPercent);
        Assert.True(job.Options.RequireVisaOrRelocation);
        Assert.Equal("17", job.QueueId);

        Assert.Equal("Acme", job.Extracted!.Company);
        Assert.Equal(new[] { "C#", "SQL" }, job.Extracted.Skills);
        Assert.Equal("Greenhouse", job.Found!.Portal);
        Assert.True(job.Found.Found);
        Assert.Equal(original.Match!.Percent, job.Match!.Percent);
        Assert.Equal(new[] { "Go" }, job.Match.MissingSkills);
        Assert.Equal(SupportLevel.Offered, job.Match.VisaSponsorship);
        Assert.Equal(SupportLevel.NotOffered, job.Match.Relocation);
        Assert.Equal("We sponsor visas; no relocation help.", job.Match.SupportEvidence);
        Assert.True(job.Match.SupportAssessed);
        Assert.Equal(ApplyResultKind.AlreadyApplied, job.Apply!.Result);
        Assert.Equal("ref-9", job.Apply.Confirmation);
        Assert.Equal(HelpKind.Captcha, job.PendingHelp!.Kind);
        Assert.Equal("ab12cd34", job.PendingHelp.Id);

        Assert.True(job.AccountRecorded);
        Assert.True(job.LimitHit);
        Assert.True(job.LimitResetKnown);
        Assert.True(job.AutoRetry);
        Assert.Equal(3, job.LimitRetries);
        Assert.Equal(original.RetryAfterUtc, job.RetryAfterUtc);

        Assert.Equal(new[] { "Started.", "Usage limit reached: \"5-hour limit\"" }, job.Log.Select(l => l.Text));
        Assert.Equal(new[] { "info", "warn" }, job.Log.Select(l => l.Kind));
        Assert.Equal(original.Log[1].Utc, job.Log[1].Utc);
    }

    [Fact]
    public void Times_come_back_as_UTC()
    {
        using var env = new TestEnv();
        env.NewPersistence().Save(Everything());

        var job = env.NewPersistence().LoadAll().Single();

        Assert.Equal(DateTimeKind.Utc, job.CreatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, job.UpdatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, job.RetryAfterUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, job.Log[0].Utc.Kind);
        Assert.Equal(DateTimeKind.Utc, job.PendingHelp!.CreatedUtc.Kind);
    }

    [Fact]
    public void The_generated_password_is_never_written_to_the_database()
    {
        using var env = new TestEnv();
        env.NewPersistence().Save(Everything());

        Assert.Null(env.NewPersistence().LoadAll().Single().Password);
    }

    [Fact]
    public void A_new_job_has_no_stage_results_and_they_stay_empty()
    {
        using var env = new TestEnv();
        var fresh = new JobRecord { ScreenshotFile = "screenshot.jpg", OriginalFileName = "a.jpg" };
        env.NewPersistence().Save(fresh);

        var job = env.NewPersistence().LoadAll().Single();

        Assert.Null(job.Extracted);
        Assert.Null(job.Found);
        Assert.Null(job.Match);
        Assert.Null(job.Apply);
        Assert.Null(job.PendingHelp);
        Assert.Null(job.QueueId);
        Assert.Null(job.RetryAfterUtc);
        Assert.Empty(job.Log);
        Assert.Equal(JobStatus.Queued, job.Status);
    }

    [Fact]
    public void Saving_again_updates_the_same_row()
    {
        using var env = new TestEnv();
        var persistence = env.NewPersistence();
        var job = Everything();
        persistence.Save(job);

        job.Status = JobStatus.Applied;
        job.Found = null;                                      // a stage result can be cleared again (a retry searches anew)
        job.Log.Add(new LogEntry { Kind = "info", Text = "Result: Submitted." });
        persistence.Save(job);

        var saved = Assert.Single(env.NewPersistence().LoadAll());
        Assert.Equal(JobStatus.Applied, saved.Status);
        Assert.Null(saved.Found);
        Assert.Equal(3, saved.Log.Count);
    }

    [Fact]
    public void A_row_that_was_deleted_behind_the_apps_back_is_put_back_by_the_next_save()
    {
        using var env = new TestEnv();
        var persistence = env.NewPersistence();
        var job = Everything();
        persistence.Save(job);
        using (var db = env.Factory.CreateDbContext()) db.Jobs.ExecuteDelete();

        job.StatusDetail = "still here";
        persistence.Save(job);

        Assert.Equal("still here", Assert.Single(env.NewPersistence().LoadAll()).StatusDetail);
    }

    [Fact]
    public void Delete_removes_only_that_job_and_the_ids_list_what_is_saved()
    {
        using var env = new TestEnv();
        var persistence = env.NewPersistence();
        var keep = new JobRecord();
        var drop = new JobRecord();
        persistence.Save(keep);
        persistence.Save(drop);

        persistence.Delete(drop.Id);
        persistence.Delete(drop.Id);   // already gone: not an error

        Assert.Equal(new[] { keep.Id }, persistence.Ids());
    }

    [Fact]
    public void Removing_a_job_from_the_store_removes_it_from_the_database_too()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var job = new JobRecord();
        store.Add(job);
        Assert.Single(env.NewPersistence().Ids());

        store.Remove(job.Id);

        Assert.Empty(env.NewPersistence().Ids());
        Assert.False(env.NewStore().Exists(job.Id));
    }

    [Fact]
    public void The_stores_changes_reach_the_database_when_it_flushes()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var job = new JobRecord();
        store.Add(job);

        store.SetStatus(job.Id, JobStatus.Searching, "Looking");
        store.Log(job.Id, "info", "hello");
        Assert.Equal(JobStatus.Queued, env.NewStore().Read(job.Id, r => r.Status));   // not flushed yet

        store.Flush();

        var reloaded = env.NewStore();
        Assert.Equal(JobStatus.Searching, reloaded.Read(job.Id, r => r.Status));
        Assert.Equal("hello", reloaded.Read(job.Id, r => r.Log.Single().Text));
    }
}

public class DatabaseSchemaTests
{
    // Never connects: these only look at the model and the migrations, using the SQL Server provider the app runs on.
    private static JobApplierDbContext SqlServerContext() => new(JobApplierDbOptions.Create("Server=nowhere;Database=none"));

    [Fact]
    public void The_migrations_are_up_to_date_with_the_model()
    {
        using var db = SqlServerContext();

        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges(),
            "The model changed since the last migration: run `dotnet ef migrations add <Name> --project src/JobApplier --output-dir Persistence/Migrations`.");
    }

    [Fact]
    public void The_schema_is_one_Jobs_table_with_json_documents_in_plain_text_columns()
    {
        using var db = SqlServerContext();

        var script = db.Database.GetService<IMigrator>().GenerateScript();

        Assert.Contains("CREATE TABLE [Jobs]", script);
        Assert.Contains("[Extracted] nvarchar(max) NULL", script);
        Assert.Contains("[Log] nvarchar(max) NOT NULL", script);
        Assert.Contains("[Status] nvarchar(20) NOT NULL", script);
        Assert.Contains("[CreatedUtc] datetime2 NOT NULL", script);
        Assert.DoesNotContain(" json ", script);          // SQL Server 2022 (and Express) has no json type
        Assert.DoesNotContain("[Password]", script);
    }

    [Fact]
    public void The_connection_string_comes_from_the_configuration_or_falls_back_to_local_SQL_Express()
    {
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:JobApplier"] = "Server=other;Database=x" }).Build();
        var empty = new ConfigurationBuilder().Build();

        Assert.Equal("Server=other;Database=x", DatabaseStartup.ConnectionString(configured));
        Assert.Contains(@"localhost\SQLEXPRESS", DatabaseStartup.ConnectionString(empty));
    }
}

public class LegacyJobImporterTests
{
    private static string WriteLegacy(TestEnv env, JobRecord job)
    {
        var file = Path.Combine(env.Paths.JobFolder(job.Id), LegacyJobImporter.FileName);
        JsonFile.Write(file, job);
        return file;
    }

    [Fact]
    public void Jobs_from_the_old_job_json_files_are_moved_into_the_database_once()
    {
        using var env = new TestEnv();
        var done = new JobRecord { Status = JobStatus.Applied, Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" } };
        var running = new JobRecord { Status = JobStatus.Searching, Found = new FoundPosting { Url = "https://x.example" } };
        var doneFile = WriteLegacy(env, done);
        WriteLegacy(env, running);
        var persistence = env.NewPersistence();

        Assert.Equal(2, LegacyJobImporter.Import(env.Paths, persistence, NullLogger.Instance));

        var store = env.NewStore();
        Assert.Equal("Acme", store.Read(done.Id, r => r.Extracted!.Company));
        Assert.Equal(JobStatus.Failed, store.Read(running.Id, r => r.Status));      // unfinished in the old file: interrupted, kept for Retry
        Assert.Equal("https://x.example", store.Read(running.Id, r => r.Found!.Url));
        Assert.False(File.Exists(doneFile));                                                   // not read again ...
        Assert.True(File.Exists(Path.ChangeExtension(doneFile, ".json.imported")));            // ... but kept as a backup
        Assert.Equal(0, LegacyJobImporter.Import(env.Paths, persistence, NullLogger.Instance));
    }

    [Fact]
    public void A_job_an_earlier_version_left_unfinished_is_imported_as_interrupted_and_not_run_again_by_itself()
    {
        using var env = new TestEnv();
        var midway = new JobRecord
        {
            Status = JobStatus.Applying,
            Options = new JobOptions { AutoSubmit = true },
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" },
            Found = new FoundPosting { Found = true, Url = "https://acme.example/jobs/1" },
            PendingHelp = new HelpRequest { Kind = HelpKind.Review, Message = "Approve?" }
        };
        WriteLegacy(env, midway);
        LegacyJobImporter.Import(env.Paths, env.NewPersistence(), NullLogger.Instance);

        // The first start of the new version: nothing from the old files may begin an application on its own.
        var rig = new Rig(env);
        var queued = rig.Runner.RecoverAfterRestart();

        Assert.Equal(0, queued);
        Assert.Empty(rig.Queue.Enqueued);
        Assert.Equal(JobStatus.Failed, rig.Store.Read(midway.Id, r => r.Status));
        Assert.Contains("Press Retry", rig.Store.Read(midway.Id, r => r.StatusDetail)!);
        Assert.Null(rig.Store.Read(midway.Id, r => r.PendingHelp));
        Assert.Equal("Acme", rig.Store.Read(midway.Id, r => r.Extracted!.Company));      // what it had done is kept, so Retry continues from there
        Assert.True(rig.Store.Read(midway.Id, r => r.Options.AutoSubmit));
        Assert.True(rig.Runner.Retry(midway.Id));                                         // and the user can still continue it
    }

    [Fact]
    public void A_job_already_in_the_database_is_not_overwritten_by_an_older_file()
    {
        using var env = new TestEnv();
        var job = new JobRecord { Status = JobStatus.Failed, StatusDetail = "in the database" };
        env.NewPersistence().Save(job);
        job.StatusDetail = "in the old file";
        job.Status = JobStatus.Queued;
        WriteLegacy(env, job);

        LegacyJobImporter.Import(env.Paths, env.NewPersistence(), NullLogger.Instance);

        var saved = env.NewStore();
        Assert.Equal("in the database", saved.Read(job.Id, r => r.StatusDetail));
        Assert.Equal(JobStatus.Failed, saved.Read(job.Id, r => r.Status));
    }

    [Fact]
    public void A_file_that_is_not_a_job_is_left_alone()
    {
        using var env = new TestEnv();
        var folder = Path.Combine(env.Paths.JobsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, LegacyJobImporter.FileName);
        File.WriteAllText(file, "{ this is not json");

        Assert.Equal(0, LegacyJobImporter.Import(env.Paths, env.NewPersistence(), NullLogger.Instance));
        Assert.True(File.Exists(file));
        Assert.Empty(env.NewPersistence().Ids());
    }
}

public class HangfireWiringTests
{
    [Fact]
    public void A_job_runs_on_the_jobs_queue_and_is_never_retried_by_hangfire_itself()
    {
        var job = Job.FromExpression<JobRunner>(runner => runner.RunQueuedJobAsync(Guid.NewGuid(), CancellationToken.None));

        var filters = JobFilterProviders.Providers.GetFilters(job).Select(f => f.Instance).ToList();

        Assert.Equal(nameof(JobRunner.RunQueuedJobAsync), job.Method.Name);
        Assert.Equal("jobs", Assert.Single(filters.OfType<QueueAttribute>()).Queue);
        // The runner keeps its own account of failures (the job record), and a retry by Hangfire would run a failed job a second time.
        Assert.Equal(0, Assert.Single(filters.OfType<AutomaticRetryAttribute>()).Attempts);
    }

    [Fact]
    public void The_recurring_limit_check_runs_on_the_default_queue()
    {
        var job = Job.FromExpression<JobRunner>(runner => runner.CheckDueRetries());

        var filters = JobFilterProviders.Providers.GetFilters(job).Select(f => f.Instance).ToList();

        Assert.Equal("default", Assert.Single(filters.OfType<QueueAttribute>()).Queue);
        Assert.Equal(0, Assert.Single(filters.OfType<AutomaticRetryAttribute>()).Attempts);
    }

    [Fact]
    public void The_server_takes_small_housekeeping_work_before_jobs()
    {
        Assert.Equal(new[] { "default", "jobs" }, JobQueues.All);
    }
}

public class SlotGateTests
{
    private static async Task<bool> Done(Task task, int milliseconds = 3000) => await Task.WhenAny(task, Task.Delay(milliseconds)) == task;

    [Fact]
    public async Task No_more_than_the_limit_are_let_in_and_a_released_slot_goes_to_the_next()
    {
        var gate = new SlotGate();
        using var a = await gate.EnterAsync(() => 2, CancellationToken.None);
        using var b = await gate.EnterAsync(() => 2, CancellationToken.None);

        var third = gate.EnterAsync(() => 2, CancellationToken.None);
        await Task.Delay(500);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, gate.InUse);

        a.Dispose();

        Assert.True(await Done(third));
        Assert.Equal(2, gate.InUse);
        (await third).Dispose();
    }

    [Fact]
    public async Task Waiting_jobs_are_let_in_in_the_order_they_asked()
    {
        var gate = new SlotGate();
        var first = await gate.EnterAsync(() => 1, CancellationToken.None);
        var order = new List<int>();
        var waiters = Enumerable.Range(1, 3).Select(async i =>
        {
            using var slot = await gate.EnterAsync(() => 1, CancellationToken.None);
            lock (order) order.Add(i);
            await Task.Delay(50);
        }).ToList();                                      // each call has queued itself before the next one starts

        first.Dispose();

        Assert.True(await Done(Task.WhenAll(waiters), 8000));
        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public async Task A_higher_limit_lets_waiting_jobs_in_without_anyone_leaving()
    {
        var gate = new SlotGate();
        var limit = 1;
        using var a = await gate.EnterAsync(() => limit, CancellationToken.None);
        var second = gate.EnterAsync(() => limit, CancellationToken.None);
        await Task.Delay(500);
        Assert.False(second.IsCompleted);

        limit = 2;   // the user changed "Jobs at the same time" in Settings

        Assert.True(await Done(second));
        (await second).Dispose();
    }

    [Fact]
    public async Task A_job_that_stops_waiting_gives_up_its_place()
    {
        var gate = new SlotGate();
        using var a = await gate.EnterAsync(() => 1, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var leaving = gate.EnterAsync(() => 1, cancel.Token);
        var staying = gate.EnterAsync(() => 1, CancellationToken.None);
        Assert.Equal(2, gate.Waiting);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        Assert.Equal(1, gate.Waiting);

        a.Dispose();
        Assert.True(await Done(staying));
        (await staying).Dispose();
        Assert.Equal(0, gate.InUse);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_frees_only_one_slot()
    {
        var gate = new SlotGate();
        var lease = await gate.EnterAsync(() => 2, CancellationToken.None);
        using var other = await gate.EnterAsync(() => 2, CancellationToken.None);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, gate.InUse);
    }
}
