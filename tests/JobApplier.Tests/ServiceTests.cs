using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobApplier.Agents;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Mcp;
using JobApplier.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class JobStoreTests
{
    [Fact]
    public void Log_masks_the_jobs_password()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var job = new JobRecord { Password = "Zq7!test-SECRET" };
        store.Add(job);

        store.Log(job.Id, "tool", "typed Zq7!test-SECRET into the box");
        store.SetStatus(job.Id, JobStatus.Failed, "site rejected Zq7!test-SECRET");

        var (log, detail) = store.Read(job.Id, r => (r.Log.Single().Text, r.StatusDetail));
        Assert.DoesNotContain("Zq7!test-SECRET", log);
        Assert.DoesNotContain("Zq7!test-SECRET", detail);
    }

    [Fact]
    public void Jobs_survive_a_restart_and_unfinished_ones_are_left_for_the_runner_to_continue()
    {
        using var env = new TestEnv();
        var first = env.NewStore();
        var running = new JobRecord { Status = JobStatus.Applying, Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" } };
        var done = new JobRecord { Status = JobStatus.Applied };
        first.Add(running);
        first.Add(done);
        first.Flush();

        var second = env.NewStore();

        // Not failed any more: JobRunner.RecoverAfterRestart queues it again and it continues from the stage it had reached.
        Assert.Equal(JobStatus.Applying, second.Read(running.Id, r => r.Status));
        Assert.Equal("Acme", second.Read(running.Id, r => r.Extracted!.Company));
        Assert.Equal(JobStatus.Applied, second.Read(done.Id, r => r.Status));
    }

    [Fact]
    public void Log_is_capped()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var job = new JobRecord();
        store.Add(job);
        for (var i = 0; i < 800; i++) store.Log(job.Id, "info", "line " + i);

        var log = store.Read(job.Id, r => r.Log.ToList())!;
        Assert.Equal(600, log.Count);
        Assert.Equal("line 799", log[^1].Text);
    }

    [Fact]
    public void Remove_deletes_the_job_folder()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var job = new JobRecord();
        Directory.CreateDirectory(env.Paths.JobFolder(job.Id));
        store.Add(job);

        Assert.True(store.Remove(job.Id));
        Assert.False(Directory.Exists(env.Paths.JobFolder(job.Id)));
        Assert.False(store.Exists(job.Id));
    }
}

public class JobStoreClearTests
{
    [Fact]
    public void RemoveWhere_deletes_only_the_accepted_jobs_and_their_folders()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var failedA = new JobRecord { Status = JobStatus.Failed };
        var failedB = new JobRecord { Status = JobStatus.Failed };
        var running = new JobRecord { Status = JobStatus.Applying };
        var applied = new JobRecord { Status = JobStatus.Applied };
        var notFound = new JobRecord { Status = JobStatus.NotFound };
        foreach (var job in new[] { failedA, failedB, running, applied, notFound })
        {
            Directory.CreateDirectory(env.Paths.JobFolder(job.Id));
            File.WriteAllText(Path.Combine(env.Paths.JobFolder(job.Id), "screenshot.png"), "x");
            store.Add(job);
        }

        var removed = store.RemoveWhere(r => r.Status == JobStatus.Failed);

        Assert.Equal(2, removed);
        Assert.False(store.Exists(failedA.Id));
        Assert.False(store.Exists(failedB.Id));
        Assert.False(Directory.Exists(env.Paths.JobFolder(failedA.Id)));
        Assert.False(Directory.Exists(env.Paths.JobFolder(failedB.Id)));
        foreach (var kept in new[] { running, applied, notFound })
        {
            Assert.True(store.Exists(kept.Id));
            Assert.True(Directory.Exists(env.Paths.JobFolder(kept.Id)));
        }
    }

    [Fact]
    public void A_failed_job_that_is_still_active_can_be_left_out_by_the_rule()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var stopped = new JobRecord { Status = JobStatus.Failed };
        var stillWinding = new JobRecord { Status = JobStatus.Failed };
        store.Add(stopped);
        store.Add(stillWinding);

        var removed = store.RemoveWhere(r => r.Status == JobStatus.Failed && r.Id != stillWinding.Id);

        Assert.Equal(1, removed);
        Assert.True(store.Exists(stillWinding.Id));
    }

    [Fact]
    public void Nothing_to_remove_is_not_an_error()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        store.Add(new JobRecord { Status = JobStatus.Applied });

        Assert.Equal(0, store.RemoveWhere(r => r.Status == JobStatus.Failed));
    }
}

public class HumanHelpBrokerTests
{
    private static (HumanHelpBroker Broker, JobStore Store, Guid Id, TestEnv Env) Create()
    {
        var env = new TestEnv();
        var store = env.NewStore();
        var job = new JobRecord();
        store.Add(job);
        return (new HumanHelpBroker(store), store, job.Id, env);
    }

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task First_call_creates_a_request_visible_to_the_ui_and_reports_pending()
    {
        var (broker, store, id, env) = Create();
        using var _ = env;

        var result = await broker.WaitAsync(id, HelpKind.Captcha, "Solve the captcha", null, Short, Long, CancellationToken.None);

        Assert.Equal(HelpWaitStatus.Pending, result.Status);
        var pending = store.Read(id, r => r.PendingHelp)!;
        Assert.Equal(result.Request!.Id, pending.Id);
        Assert.Equal(HelpKind.Captcha, pending.Kind);
    }

    [Fact]
    public async Task Resolving_unblocks_the_next_poll_even_if_nobody_was_waiting()
    {
        var (broker, store, id, env) = Create();
        using var _ = env;
        var first = await broker.WaitAsync(id, HelpKind.Question, "Notice period?", null, Short, Long, CancellationToken.None);

        // The user answers between two polls of the agent.
        var resolved = broker.Resolve(id, first.Request!.Id, new HelpResponse("answer", "One month"));
        var second = await broker.WaitAsync(id, HelpKind.Question, "", first.Request.Id, Short, Long, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(HelpWaitStatus.Answered, second.Status);
        Assert.Equal("One month", second.Response!.Text);
        Assert.Null(store.Read(id, r => r.PendingHelp));
        Assert.False(broker.IsWaiting(id));
    }

    [Fact]
    public async Task Resolving_while_waiting_returns_immediately()
    {
        var (broker, store, id, env) = Create();
        using var _e = env;
        var wait = broker.WaitAsync(id, HelpKind.Captcha, "captcha", null, TimeSpan.FromSeconds(30), Long, CancellationToken.None);

        await Task.Delay(100);
        // The UI learns the request id from the job, exactly like this.
        var requestId = store.Read(id, r => r.PendingHelp!.Id)!;
        broker.Resolve(id, requestId, new HelpResponse("done", ""));

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HelpWaitStatus.Answered, result.Status);
    }

    [Fact]
    public async Task Resolving_with_a_wrong_request_id_is_rejected()
    {
        var (broker, _, id, env) = Create();
        using var _e = env;
        await broker.WaitAsync(id, HelpKind.Captcha, "captcha", null, Short, Long, CancellationToken.None);

        Assert.Null(broker.Resolve(id, "nope", new HelpResponse("done", "")));
        Assert.True(broker.IsWaiting(id));
    }

    [Fact]
    public async Task Gives_up_once_the_overall_timeout_has_passed_and_clears_the_request()
    {
        var (broker, store, id, env) = Create();
        using var _e = env;

        // The deadline (10 ms) is already over when the first slice (80 ms) ends.
        var result = await broker.WaitAsync(id, HelpKind.Captcha, "captcha", null, Short, TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Equal(HelpWaitStatus.TimedOut, result.Status);
        Assert.Null(store.Read(id, r => r.PendingHelp));
        Assert.False(broker.IsWaiting(id));

        // A stale poll for the abandoned request is harmless.
        var stale = await broker.WaitAsync(id, HelpKind.Captcha, "", result.Request!.Id, Short, Long, CancellationToken.None);
        Assert.Equal(HelpWaitStatus.Unknown, stale.Status);
    }

    [Fact]
    public async Task Cancelling_the_job_releases_the_agent()
    {
        var (broker, store, id, env) = Create();
        using var _e = env;
        var first = await broker.WaitAsync(id, HelpKind.Review, "review", null, Short, Long, CancellationToken.None);

        broker.CancelJob(id);
        var second = await broker.WaitAsync(id, HelpKind.Review, "", first.Request!.Id, Short, Long, CancellationToken.None);

        Assert.Equal(HelpWaitStatus.Cancelled, second.Status);
        Assert.Null(store.Read(id, r => r.PendingHelp));
    }

    [Fact]
    public async Task An_aborted_request_is_not_an_error_and_the_wait_carries_on_with_the_same_id()
    {
        var (broker, store, id, env) = Create();
        using var _e = env;
        var first = await broker.WaitAsync(id, HelpKind.Review, "review", null, Short, Long, CancellationToken.None);

        // The agent's HTTP request is aborted while it is waiting (the client gave up): no exception, request still pending.
        using var aborted = new CancellationTokenSource();
        var wait = broker.WaitAsync(id, HelpKind.Review, "", first.Request!.Id, TimeSpan.FromSeconds(30), Long, aborted.Token);
        aborted.Cancel();
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HelpWaitStatus.Pending, result.Status);
        Assert.True(broker.IsWaiting(id));
        Assert.Equal(first.Request.Id, store.Read(id, r => r.PendingHelp!.Id));
    }

    [Fact]
    public async Task An_answer_that_arrives_with_an_aborted_request_is_kept_for_the_next_poll()
    {
        var (broker, _, id, env) = Create();
        using var _e = env;
        var first = await broker.WaitAsync(id, HelpKind.Review, "review", null, Short, Long, CancellationToken.None);

        // The user approves, but the request that would have carried the answer was already aborted.
        broker.Resolve(id, first.Request!.Id, new HelpResponse("approve", ""));
        var aborted = await broker.WaitAsync(id, HelpKind.Review, "", first.Request.Id, Short, Long, new CancellationToken(canceled: true));
        var retry = await broker.WaitAsync(id, HelpKind.Review, "", first.Request.Id, Short, Long, CancellationToken.None);

        Assert.Equal(HelpWaitStatus.Pending, aborted.Status);
        Assert.Equal(HelpWaitStatus.Answered, retry.Status);
        Assert.Equal("approve", retry.Response!.Action);
    }

    [Fact]
    public async Task Unknown_request_id_is_reported_not_thrown()
    {
        var (broker, _, id, env) = Create();
        using var _e = env;
        var result = await broker.WaitAsync(id, HelpKind.Captcha, "", "stale", Short, Long, CancellationToken.None);
        Assert.Equal(HelpWaitStatus.Unknown, result.Status);
    }
}

public class AccountDocumentTests
{
    private static AccountRecord Record(string company, string password = "Pw!12345678abcd") => new()
    {
        Company = company, Role = "Engineer", Site = "jobs." + company.ToLowerInvariant().Replace(' ', '-') + ".test", Email = "jane@example.com", Password = password
    };

    /// <summary>The accounts table (the first one); the applications table follows it.</summary>
    private static List<List<string>> ReadTable(string path, int index = 0)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var tables = doc.MainDocumentPart!.Document!.Body!.Descendants<Table>().ToList();
        Assert.Equal(2, tables.Count);
        return tables[index].Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();
    }

    private static ApplicationRecord Application(string company, string url, ApplyResultKind result = ApplyResultKind.Submitted) => new()
    {
        Company = company, Role = "Engineer", Url = url, Result = result
    };

    [Fact]
    public void Lists_each_applied_posting_with_its_url_in_a_second_table()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "accounts.docx");

        AccountsDocument.Write(path, new[] { Record("Acme") }, new[]
        {
            Application("Acme", "https://boards.greenhouse.io/acme/jobs/1"),
            Application("Globex", "https://jobs.lever.co/globex/abc", ApplyResultKind.AlreadyApplied)
        });

        var rows = ReadTable(path, 1);
        Assert.Equal(new[] { "Company", "Role", "Posting URL", "Result", "Applied" }, rows[0]);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "Acme", "Engineer", "https://boards.greenhouse.io/acme/jobs/1", "Submitted" }, rows[1].Take(4));
        Assert.Equal(new[] { "Globex", "Engineer", "https://jobs.lever.co/globex/abc", "Already applied" }, rows[2].Take(4));
        Assert.Equal(2, ReadTable(path).Count); // the accounts table is unchanged
    }

    [Fact]
    public void Shows_a_placeholder_when_nothing_has_been_applied_to_yet()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "accounts.docx");

        AccountsDocument.Write(path, new[] { Record("Acme") });

        Assert.Equal("No applications yet.", ReadTable(path, 1).Last()[0]);
    }

    [Fact]
    public void Writes_company_email_and_password_into_a_table()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "accounts.docx");

        AccountsDocument.Write(path, new[] { Record("Acme"), Record("Globex", "Other!pass-9999xx") });

        var rows = ReadTable(path);
        Assert.Equal(new[] { "Company", "Role", "Site", "E-mail", "Password", "Created" }, rows[0]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Acme", rows[1][0]);
        Assert.Equal("jane@example.com", rows[1][3]);
        Assert.Equal("Pw!12345678abcd", rows[1][4]);
        Assert.Equal("Other!pass-9999xx", rows[2][4]);
    }

    [Fact]
    public void Overwrites_in_place_and_leaves_no_temp_files()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "accounts.docx");

        AccountsDocument.Write(path, new[] { Record("Acme") });
        AccountsDocument.Write(path, new[] { Record("Acme"), Record("Globex") });

        Assert.Equal(3, ReadTable(path).Count);
        Assert.Empty(Directory.GetFiles(env.Root, "*.tmp"));
    }

    [Fact]
    public void An_empty_list_still_produces_a_valid_document()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "accounts.docx");
        AccountsDocument.Write(path, Array.Empty<AccountRecord>());
        Assert.Contains("No accounts yet.", ReadTable(path).Last()[0]);
    }

    [Fact]
    public void Keeps_special_characters_intact()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "accounts.docx");
        AccountsDocument.Write(path, new[] { Record("R&D <Labs> \"Co\"", "a&b<c>*?%@!xyz1234") });

        var row = ReadTable(path)[1];
        Assert.Equal("R&D <Labs> \"Co\"", row[0]);
        Assert.Equal("a&b<c>*?%@!xyz1234", row[4]);
    }

    [Fact]
    public void A_locked_document_is_reported_and_the_data_is_kept()
    {
        using var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "accounts.docx") });
        var service = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
        service.Add(Record("Acme"));

        // Simulate Word holding the file open.
        using (new FileStream(service.DocumentPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service.Add(Record("Globex"));
            Assert.NotNull(service.DocumentError);
        }

        Assert.Equal(2, service.All().Count);
        Assert.Null(service.RewriteDocument());
        Assert.Equal(3, ReadTable(service.DocumentPath).Count);
    }
}

public class AccountServiceTests
{
    private static AccountService Create(TestEnv env)
    {
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "a.docx") });
        return new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
    }

    [Fact]
    public void Two_jobs_on_the_same_site_share_one_account_row_and_list_both_roles()
    {
        using var env = new TestEnv();
        var service = Create(env);

        service.Add(new AccountRecord { JobId = Guid.NewGuid(), Site = "acme.wd5.myworkdayjobs.com", Company = "Acme", Role = "Backend Engineer", Email = "a@b.c", Password = "p1-xxxxxxxxxx" });
        service.Add(new AccountRecord { JobId = Guid.NewGuid(), Site = "https://ACME.wd5.myworkdayjobs.com/en-US/x", Company = "Acme", Role = "Data Engineer", Email = "A@B.C", Password = "p1-xxxxxxxxxx", Note = "verified" });

        var row = Assert.Single(service.All());
        Assert.Equal("Backend Engineer; Data Engineer", row.Role);
        Assert.Equal("verified", row.Note);
    }

    [Fact]
    public void Different_sites_or_different_emails_are_different_accounts()
    {
        using var env = new TestEnv();
        var service = Create(env);

        service.Add(new AccountRecord { Site = "jobs.acme.test", Email = "a@b.c", Password = "pw-1234567890" });
        service.Add(new AccountRecord { Site = "jobs.globex.test", Email = "a@b.c", Password = "pw-1234567890" });
        service.Add(new AccountRecord { Site = "jobs.acme.test", Email = "other@b.c", Password = "pw-1234567890" });

        Assert.Equal(3, service.All().Count);
    }

    [Fact]
    public void FindForSite_lets_a_later_job_reuse_the_password_of_an_existing_account()
    {
        using var env = new TestEnv();
        var service = Create(env);
        service.Add(new AccountRecord { Site = "acme.wd5.myworkdayjobs.com", Email = "jane@example.com", Password = "First!pass-1234" });

        var found = service.FindForSite("https://www.acme.wd5.myworkdayjobs.com/en/job/123", "Jane@Example.com");

        Assert.Equal("First!pass-1234", found!.Password);
        Assert.Null(service.FindForSite("https://other.example/job", "jane@example.com"));
        Assert.Null(service.FindForSite("https://acme.wd5.myworkdayjobs.com/", "someone-else@example.com"));
    }

    [Theory]
    [InlineData("https://www.Jobs.Acme.com/careers/1?x=2", "jobs.acme.com")]
    [InlineData("jobs.acme.com", "jobs.acme.com")]
    [InlineData("WWW.acme.com", "acme.com")]
    [InlineData("  http://acme.com  ", "acme.com")]
    [InlineData("not a site", "not a site")]
    [InlineData("", "")]
    public void HostOf_normalises_site_addresses(string input, string expected) =>
        Assert.Equal(expected, AccountService.HostOf(input));

    [Fact]
    public void Accounts_persist_across_restarts()
    {
        using var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "a.docx") });
        new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance)
            .Add(new AccountRecord { JobId = Guid.NewGuid(), Site = "x", Company = "Acme", Email = "a@b.c", Password = "pw-1234567890" });

        var reloaded = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
        Assert.Equal("pw-1234567890", Assert.Single(reloaded.All()).Password);
    }

    [Fact]
    public void An_applied_posting_is_found_again_whatever_the_tracking_parameters()
    {
        using var env = new TestEnv();
        var service = Create(env);
        service.RecordApplication(new ApplicationRecord { JobId = Guid.NewGuid(), Company = "Acme", Role = "Dev", Url = "https://boards.greenhouse.io/acme/jobs/123" });

        var found = service.FindApplication(Guid.NewGuid(), "https://www.boards.greenhouse.io/acme/jobs/123/?utm_source=linkedin");

        Assert.Equal("Acme", found!.Company);
        Assert.Null(service.FindApplication(Guid.NewGuid(), "https://boards.greenhouse.io/acme/jobs/124"));
        Assert.Null(service.FindApplication(Guid.NewGuid(), null));
        Assert.Null(service.FindApplication(Guid.NewGuid(), "not a url"));
    }

    [Fact]
    public void A_job_is_not_a_duplicate_of_its_own_entry()
    {
        using var env = new TestEnv();
        var service = Create(env);
        var job = Guid.NewGuid();
        service.RecordApplication(new ApplicationRecord { JobId = job, Url = "https://acme.com/jobs/1" });

        Assert.Null(service.FindApplication(job, "https://acme.com/jobs/1"));
    }

    [Fact]
    public void Recording_the_same_posting_twice_keeps_one_row_with_the_first_date()
    {
        using var env = new TestEnv();
        var service = Create(env);
        var first = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        service.RecordApplication(new ApplicationRecord { Url = "https://acme.com/jobs/1", AppliedUtc = first });
        service.RecordApplication(new ApplicationRecord { Url = "https://acme.com/jobs/1?utm_source=x", AppliedUtc = first.AddDays(3) });

        Assert.Equal(first, Assert.Single(service.Applications()).AppliedUtc);
    }

    [Fact]
    public void Applications_persist_across_restarts_and_reach_the_document()
    {
        using var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "a.docx") });
        new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance)
            .RecordApplication(new ApplicationRecord { Company = "Acme", Role = "Dev", Url = "https://acme.com/jobs/1" });

        var reloaded = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);

        Assert.NotNull(reloaded.FindApplication(Guid.NewGuid(), "https://acme.com/jobs/1"));
        Assert.Null(reloaded.RewriteDocument());
        using var doc = WordprocessingDocument.Open(reloaded.DocumentPath, false);
        Assert.Contains("https://acme.com/jobs/1", doc.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public void A_locked_document_still_remembers_the_application()
    {
        using var env = new TestEnv();
        var service = Create(env);
        service.RecordApplication(new ApplicationRecord { Url = "https://acme.com/jobs/1" }); // creates the document

        using (new FileStream(service.DocumentPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service.RecordApplication(new ApplicationRecord { Url = "https://acme.com/jobs/2" });
            Assert.NotNull(service.DocumentError);
        }

        Assert.Equal(2, service.Applications().Count);
        Assert.Equal(2, JsonFile.Read<List<ApplicationRecord>>(env.Paths.ApplicationsFile)!.Count);
    }
}

public class ResumeFileTests
{
    [Theory]
    [InlineData("Jane Doe - Resume.pdf", "Jane Doe - Resume.pdf")]
    [InlineData("Jane_Doe_CV.PDF", "Jane_Doe_CV.pdf")]                       // extension is normalised, the name is not
    [InlineData(@"C:\Users\jane\Documents\My CV.docx", "My CV.docx")]       // some browsers send the whole path
    [InlineData("../../evil.pdf", "evil.pdf")]
    [InlineData("a<b>c:d|e?.pdf", "a_b_c_d_e_.pdf")]
    [InlineData(".pdf", "resume.pdf")]
    [InlineData("", "resume")]
    public void The_users_file_name_is_kept_but_made_safe(string original, string expected) =>
        Assert.Equal(expected, ProfileService.SafeFileName(original));

    [Fact]
    public void A_very_long_name_is_shortened_and_keeps_its_extension()
    {
        var name = ProfileService.SafeFileName(new string('x', 300) + ".pdf");
        Assert.Equal(104, name.Length);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public async Task The_stored_resume_has_the_users_name_and_replaces_the_old_one()
    {
        using var env = new TestEnv();
        var profile = new ProfileService(env.Paths);

        await using (var first = new MemoryStream(new byte[] { 1, 1, 1 }))
            await profile.StoreResumeAsync(first, "Old Resume.pdf", CancellationToken.None);
        await using (var second = new MemoryStream(new byte[] { 2, 2, 2 }))
        {
            var path = await profile.StoreResumeAsync(second, "Jane Doe - Resume.pdf", CancellationToken.None);

            Assert.Equal("Jane Doe - Resume.pdf", Path.GetFileName(path));
            Assert.Equal(new byte[] { 2, 2, 2 }, File.ReadAllBytes(path));
        }

        Assert.Equal("Jane Doe - Resume.pdf", Path.GetFileName(Assert.Single(Directory.GetFiles(env.Paths.ResumeFolder))));
    }

    private static (ApplyExecutor Stage, ProfileService Profile, TestEnv Env) CreateStage()
    {
        var env = new TestEnv();
        var profile = new ProfileService(env.Paths);
        var services = new PipelineServices(null!, null!, profile, null!, env.Paths);
        return (new ApplyExecutor(services), profile, env);
    }

    [Fact]
    public void The_job_gets_the_current_resume_and_nothing_else()
    {
        var (stage, profile, env) = CreateStage();
        using var _ = env;
        File.WriteAllBytes(Path.Combine(env.Paths.ResumeFolder, "Jane Doe - Resume.pdf"), new byte[] { 9, 9, 9 });
        profile.Save(new UserProfile { Email = "jane@example.com", ResumeFileName = "Jane Doe - Resume.pdf" });

        // A copy left in the workspace by an earlier run, from before the resume was replaced.
        var workspace = Path.Combine(env.Root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, "resume"));
        File.WriteAllBytes(Path.Combine(workspace, "resume", "resume.pdf"), new byte[] { 1 });

        var copy = stage.CopyResume(workspace);

        Assert.Equal(Path.Combine(workspace, "resume", "Jane Doe - Resume.pdf"), copy);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(copy));
        Assert.Equal(new[] { copy }, Directory.GetFiles(Path.Combine(workspace, "resume")));
    }
}

public class ExtractSplitTests
{
    // The four cards in a LinkedIn "jobs" sidebar.
    private static readonly ExtractedJob[] Listing =
    {
        new() { Company = "Wolters Kluwer", Title = "Senior Product Software Engineer - AI native", Location = "Barcelona (Hybrid)" },
        new() { Company = "Nextlane", Title = "Senior AI Software Engineer", Location = "Madrid (Hybrid)" },
        new() { Company = "Nextlane", Title = "Senior IA Software Engineer", Location = "Vigo (Hybrid)" },
        new() { Company = "Ingeteam", Title = "AI-AUGMENTED FULL STACK DEVELOPER", Location = "Zamudio" }
    };

    private static (ExtractExecutor Stage, JobStore Store, List<Guid> Enqueued, TestEnv Env) Create()
    {
        var env = new TestEnv();
        var store = env.NewStore();
        var enqueued = new List<Guid>();
        var services = new PipelineServices(null!, store, new ProfileService(env.Paths), null!, env.Paths, id => { enqueued.Add(id); return true; });
        return (new ExtractExecutor(services), store, enqueued, env);
    }

    private static JobRecord Parent(TestEnv env, bool autoSubmit = true, int minMatch = 60)
    {
        var parent = new JobRecord
        {
            ScreenshotFile = "screenshot.png",
            OriginalFileName = "list.png",
            Options = new JobOptions { AutoSubmit = autoSubmit, MinMatchPercent = minMatch }
        };
        Directory.CreateDirectory(env.Paths.JobFolder(parent.Id));
        File.WriteAllBytes(Path.Combine(env.Paths.JobFolder(parent.Id), parent.ScreenshotFile), new byte[] { 1, 2, 3 });
        return parent;
    }

    [Fact]
    public void Every_extra_job_in_a_list_becomes_a_queued_job_of_its_own()
    {
        var (stage, store, enqueued, env) = Create();
        using var _ = env;
        var parent = Parent(env);
        store.Add(parent);

        stage.SplitOff(parent.Id, Listing.Skip(1).ToList(), Listing.Length);

        Assert.Equal(3, enqueued.Count);
        Assert.Equal(new[] { "Senior AI Software Engineer", "Senior IA Software Engineer", "AI-AUGMENTED FULL STACK DEVELOPER" },
            enqueued.Select(id => store.Read(id, r => r.Extracted!.Title)));
        Assert.Equal(4, store.ReadAll(r => r.Id).Count); // the parent plus three
    }

    [Fact]
    public void A_split_off_job_has_its_own_copy_of_the_image_and_the_same_options()
    {
        var (stage, store, enqueued, env) = Create();
        using var _ = env;
        var parent = Parent(env, autoSubmit: true, minMatch: 60);
        store.Add(parent);

        stage.SplitOff(parent.Id, new[] { Listing[1] }, Listing.Length);

        var child = Assert.Single(enqueued);
        Assert.NotEqual(parent.Id, child);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(env.Paths.JobFolder(child), "screenshot.png")));
        Assert.Equal("list.png", store.Read(child, r => r.OriginalFileName));
        Assert.True(store.Read(child, r => r.Options.AutoSubmit));
        Assert.Equal(60, store.Read(child, r => r.Options.MinMatchPercent));

        // Deleting the child must not touch the parent's image.
        store.Remove(child);
        Assert.True(File.Exists(Path.Combine(env.Paths.JobFolder(parent.Id), "screenshot.png")));
    }

    [Fact]
    public async Task A_split_off_job_skips_reading_the_screenshot_again()
    {
        var (stage, store, enqueued, env) = Create();
        using var _ = env;
        var parent = Parent(env);
        store.Add(parent);
        stage.SplitOff(parent.Id, new[] { Listing[1] }, Listing.Length);

        // No agent exists in this test: the stage can only continue if it does not try to read the image.
        var result = await stage.HandleAsync(new StageResult(enqueued.Single(), true), null!);

        Assert.True(result.Continue);
    }

    [Fact]
    public void Repeats_and_unreadable_entries_are_dropped()
    {
        var list = new[]
        {
            Listing[0],
            new ExtractedJob { Company = "  wolters   KLUWER ", Title = "Senior Product Software Engineer - AI native", Location = "barcelona (hybrid)" }, // the selected job, shown again in the details pane
            new ExtractedJob(),                                                                                  // a cut-off card
            new ExtractedJob { Company = "Nextlane", Title = "" },                                               // company only: still a job
            Listing[1]
        };

        var jobs = ExtractExecutor.DistinctJobs(list);

        Assert.Equal(new[] { "Wolters Kluwer", "Nextlane", "Nextlane" }, jobs.Select(j => j.Company));
        Assert.Same(Listing[0], jobs[0]); // the first sighting is kept
    }

    [Fact]
    public void The_same_title_in_two_places_is_two_jobs()
    {
        var jobs = ExtractExecutor.DistinctJobs(new[]
        {
            new ExtractedJob { Company = "Nextlane", Title = "Senior AI Software Engineer", Location = "Madrid (Hybrid)" },
            new ExtractedJob { Company = "Nextlane", Title = "Senior AI Software Engineer", Location = "Vigo (Hybrid)" }
        });

        Assert.Equal(2, jobs.Count);
    }

    [Fact]
    public void Nothing_readable_gives_no_jobs()
    {
        Assert.Empty(ExtractExecutor.DistinctJobs(null));
        Assert.Empty(ExtractExecutor.DistinctJobs(new[] { new ExtractedJob() }));
    }
}

public class ApplyStageDuplicateGuardTests
{
    /// <summary>An apply stage with no agents at all: it can only get as far as the duplicate check.</summary>
    private static (ApplyExecutor Stage, JobStore Store, AccountService Accounts, TestEnv Env) Create()
    {
        var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "a.docx") });
        var profile = new ProfileService(env.Paths);
        File.WriteAllText(Path.Combine(env.Paths.ResumeFolder, "resume.txt"), "resume");
        profile.Save(new UserProfile { Email = "jane@example.com", ResumeFileName = "resume.txt" });

        var store = env.NewStore();
        var accounts = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
        return (new ApplyExecutor(new PipelineServices(null!, store, profile, accounts, env.Paths)), store, accounts, env);
    }

    private static JobRecord Job(string url) => new()
    {
        Status = JobStatus.Matching,
        Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" },
        Found = new FoundPosting { Found = true, Url = url, Company = "Acme", Title = "Dev" }
    };

    [Fact]
    public async Task A_posting_already_in_the_applications_list_is_skipped_even_though_its_job_is_gone()
    {
        var (stage, store, accounts, env) = Create();
        using var _ = env;
        accounts.RecordApplication(new ApplicationRecord { JobId = Guid.NewGuid(), Company = "Acme", Role = "Dev", Url = "https://acme.com/jobs/1" });
        var job = Job("https://acme.com/jobs/1?utm_source=linkedin");
        store.Add(job);

        var result = await stage.HandleAsync(new StageResult(job.Id, true), null!);

        Assert.False(result.Continue);
        Assert.Equal(JobStatus.Skipped, store.Read(job.Id, r => r.Status));
        Assert.Contains("Already applied", store.Read(job.Id, r => r.StatusDetail)!);
        Assert.Null(store.Read(job.Id, r => r.Apply));
    }

    [Fact]
    public async Task A_different_posting_is_not_blocked_by_the_list()
    {
        var (stage, store, accounts, env) = Create();
        using var _ = env;
        accounts.RecordApplication(new ApplicationRecord { JobId = Guid.NewGuid(), Url = "https://acme.com/jobs/1" });
        var job = Job("https://acme.com/jobs/2");
        store.Add(job);

        await stage.HandleAsync(new StageResult(job.Id, true), null!);

        // It got past the duplicate check and only failed afterwards, because this test has no agent to run.
        Assert.NotEqual(JobStatus.Skipped, store.Read(job.Id, r => r.Status));
        Assert.DoesNotContain("Already applied", store.Read(job.Id, r => r.StatusDetail)!);
    }
}

public class HelpPolicyTests
{
    [Theory]
    [InlineData(HelpKind.Question, "answer", true)]
    [InlineData(HelpKind.Question, "skip", false)]
    [InlineData(HelpKind.Confirm, "yes", false)]
    [InlineData(HelpKind.Confirm, "answer", false)]   // even if free text were sent, a one-off confirmation is never remembered
    [InlineData(HelpKind.Captcha, "done", false)]
    [InlineData(HelpKind.Verification, "done", false)]
    [InlineData(HelpKind.Review, "approve", false)]
    [InlineData(HelpKind.Other, "answer", false)]
    public void Only_answers_to_real_form_questions_are_remembered(HelpKind kind, string action, bool expected) =>
        Assert.Equal(expected, HelpPolicy.ShouldRemember(new HelpRequest { Kind = kind }, action));
}

public class PostingKeyTests
{
    [Theory]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123", "https://www.boards.greenhouse.io/acme/jobs/123/")]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123", "HTTPS://Boards.Greenhouse.io/ACME/jobs/123?utm_source=linkedin&utm_medium=x")]
    [InlineData("https://jobs.lever.co/acme/abc?lever-source=LinkedIn", "https://jobs.lever.co/acme/abc")]
    [InlineData("https://acme.com/careers?gh_jid=77&gh_src=li", "https://acme.com/careers?gh_jid=77")]
    public void The_same_posting_gets_the_same_key_whatever_the_tracking_noise(string a, string b) =>
        Assert.Equal(PostingKey.From(a), PostingKey.From(b));

    [Theory]
    [InlineData("https://acme.com/careers?gh_jid=77", "https://acme.com/careers?gh_jid=78")]       // job id in the query string
    [InlineData("https://boards.greenhouse.io/acme/jobs/1", "https://boards.greenhouse.io/acme/jobs/2")]
    [InlineData("https://acme.com/jobs/1", "https://other.com/jobs/1")]
    public void Different_postings_get_different_keys(string a, string b) =>
        Assert.NotEqual(PostingKey.From(a), PostingKey.From(b));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    public void Unusable_addresses_have_no_key(string? url) => Assert.Null(PostingKey.From(url));

    [Fact]
    public void Duplicate_is_found_only_among_jobs_that_applied_or_are_applying()
    {
        var self = Guid.NewGuid();
        var applied = Guid.NewGuid();
        var applying = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var other = Guid.NewGuid();
        const string url = "https://acme.com/jobs/1";

        Assert.Equal(applied, PostingKey.FindDuplicate(self, url, new[] { (applied, (string?)(url + "?utm_source=x"), JobStatus.Applied) }));
        Assert.Equal(applying, PostingKey.FindDuplicate(self, url, new[] { (applying, (string?)url, JobStatus.Applying) }));
        Assert.Null(PostingKey.FindDuplicate(self, url, new[] { (failed, (string?)url, JobStatus.Failed) }));          // a failed attempt may be retried
        Assert.Null(PostingKey.FindDuplicate(self, url, new[] { (other, (string?)"https://acme.com/jobs/2", JobStatus.Applied) }));
        Assert.Null(PostingKey.FindDuplicate(self, url, new[] { (self, (string?)url, JobStatus.Applying) }));          // a job is not its own duplicate
        Assert.Null(PostingKey.FindDuplicate(self, null, new[] { (applied, (string?)url, JobStatus.Applied) }));
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void Normalises_out_of_range_values()
    {
        using var env = new TestEnv();
        var service = new SettingsService(env.Paths);

        Assert.Null(service.Save(new AppSettings { MaxParallelJobs = 99, DefaultMinMatchPercent = 500, Browser = "netscape", Effort = "ludicrous", HelpTimeoutMinutes = 0 }));

        var s = service.Current;
        Assert.Equal(8, s.MaxParallelJobs);
        Assert.Equal(100, s.DefaultMinMatchPercent);
        Assert.Equal("chrome", s.Browser);
        Assert.Equal("", s.Effort);
        Assert.Equal(2, s.HelpTimeoutMinutes);
    }

    [Fact]
    public void Rejects_a_bad_accounts_document_path_and_keeps_the_old_settings()
    {
        using var env = new TestEnv();
        var service = new SettingsService(env.Paths);

        Assert.NotNull(service.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "a.txt") }));
        Assert.NotNull(service.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "missing-folder", "a.docx") }));
        Assert.Equal("", service.Current.AccountsDocumentPath);
    }

    [Fact]
    public void Defaults_the_document_to_the_desktop()
    {
        using var env = new TestEnv();
        var service = new SettingsService(env.Paths);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "JobApplier-Accounts.docx"), service.AccountsDocumentPath);
    }
}

public class JobMcpServerTests
{
    private static (JobMcpServer Server, JobStore Store, ProfileService Profile, AccountService Accounts, Guid Id, TestEnv Env) Create(JobRecord? job = null)
    {
        var (server, store, profile, accounts, id, env, _) = CreateWithBroker(job);
        return (server, store, profile, accounts, id, env);
    }

    private static (JobMcpServer Server, JobStore Store, ProfileService Profile, AccountService Accounts, Guid Id, TestEnv Env, HumanHelpBroker Broker) CreateWithBroker(JobRecord? job = null)
    {
        var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(env.Root, "accounts.docx") });
        var store = env.NewStore();
        job ??= new JobRecord { Password = "Gen!erated-pass1" };
        store.Add(job);
        var profile = new ProfileService(env.Paths);
        var p = ProfileService.Clone(profile.Current);
        p.Email = "jane@example.com";
        profile.Save(p);
        var accounts = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
        var broker = new HumanHelpBroker(store);
        var server = new JobMcpServer(store, broker, accounts, profile, settings, TimeSpan.FromMilliseconds(50));
        return (server, store, profile, accounts, job.Id, env, broker);
    }

    private static JsonObject Rpc(string method, JsonObject? p = null, int id = 1) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() };

    private static JsonObject Call(string tool, JsonObject args) =>
        Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });

    private static string Text(JsonNode? response) => (string)response!["result"]!["content"]![0]!["text"]!;

    [Fact]
    public void Only_the_exact_bearer_token_is_authorised()
    {
        var (server, _, _, _, _, env) = Create();
        using var _e = env;

        Assert.True(server.IsAuthorized("Bearer " + server.Token));
        Assert.False(server.IsAuthorized("Bearer wrong"));
        Assert.False(server.IsAuthorized(server.Token));
        Assert.False(server.IsAuthorized(""));
        Assert.False(server.IsAuthorized(null));
    }

    [Fact]
    public async Task Speaks_enough_mcp_to_connect_and_list_tools()
    {
        var (server, _, _, _, id, env) = Create();
        using var _e = env;

        var init = await server.HandleAsync(id, Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" }), default);
        Assert.Equal("2025-06-18", (string)init!["result"]!["protocolVersion"]!);
        Assert.Equal("jobapplier", (string)init["result"]!["serverInfo"]!["name"]!);

        var tools = await server.HandleAsync(id, Rpc("tools/list"), default);
        var names = ((JsonArray)tools!["result"]!["tools"]!).Select(t => (string)t!["name"]!).ToList();
        Assert.Equal(new[] { "wait_for_user", "record_account", "read_verification_email", "report_progress" }, names);
    }

    [Fact]
    public async Task Notifications_get_no_response_and_unknown_methods_get_an_error()
    {
        var (server, _, _, _, id, env) = Create();
        using var _e = env;

        Assert.Null(await server.HandleAsync(id, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, default));
        var unknown = await server.HandleAsync(id, Rpc("does/not/exist"), default);
        Assert.Equal(-32601, (int)unknown!["error"]!["code"]!);
    }

    [Fact]
    public async Task Batches_are_answered_in_one_array()
    {
        var (server, _, _, _, id, env) = Create();
        using var _e = env;
        var batch = new JsonArray(Rpc("ping", id: 1), new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, Rpc("ping", id: 2));

        var response = (JsonArray)(await server.HandleAsync(id, batch, default))!;
        Assert.Equal(2, response.Count);
    }

    [Fact]
    public async Task record_account_saves_the_profile_email_and_the_generated_password_without_returning_it()
    {
        var (server, store, _, accounts, id, env) = Create();
        using var _e = env;

        var response = await server.HandleAsync(id, Call("record_account", new JsonObject { ["company"] = "Acme", ["site_url"] = "https://jobs.acme.test" }), default);

        var saved = Assert.Single(accounts.All());
        Assert.Equal("jane@example.com", saved.Email);
        Assert.Equal("Gen!erated-pass1", saved.Password);
        Assert.Equal("Acme", saved.Company);
        Assert.DoesNotContain("Gen!erated-pass1", response!.ToJsonString());
        Assert.True(store.Read(id, r => r.AccountRecorded));
    }

    [Fact]
    public async Task record_account_without_a_profile_email_is_a_tool_error()
    {
        var (server, _, profile, accounts, id, env) = Create();
        using var _e = env;
        var p = ProfileService.Clone(profile.Current);
        p.Email = "";
        profile.Save(p);

        var response = await server.HandleAsync(id, Call("record_account", new JsonObject { ["company"] = "Acme", ["site_url"] = "x" }), default);

        Assert.True((bool)response!["result"]!["isError"]!);
        Assert.Empty(accounts.All());
    }

    [Fact]
    public async Task wait_for_user_tells_the_model_to_call_again_while_the_user_has_not_answered()
    {
        var (server, store, _, _, id, env) = Create();
        using var _e = env;

        var response = await server.HandleAsync(id, Call("wait_for_user", new JsonObject { ["kind"] = "captcha", ["message"] = "Solve it" }), default);

        var requestId = store.Read(id, r => r.PendingHelp!.Id);
        Assert.Contains("not responded yet", Text(response));
        Assert.Contains($"request_id=\"{requestId}\"", Text(response));
    }

    [Fact]
    public async Task wait_for_user_passes_the_answer_back_to_the_model()
    {
        var (server, store, _, _, id, env, broker) = CreateWithBroker();
        using var _e = env;
        await server.HandleAsync(id, Call("wait_for_user", new JsonObject { ["kind"] = "question", ["message"] = "Notice period?" }), default);
        var requestId = store.Read(id, r => r.PendingHelp!.Id)!;

        broker.Resolve(id, requestId, new HelpResponse("answer", "Two weeks"));

        var response = await server.HandleAsync(id, Call("wait_for_user", new JsonObject { ["kind"] = "question", ["message"] = "", ["request_id"] = requestId }), default);
        Assert.Equal("The user answered: Two weeks", Text(response));
    }

    [Fact]
    public async Task An_approval_reaches_a_model_that_timed_out_and_re_asked_without_the_request_id()
    {
        var (server, store, _, _, id, env, broker) = CreateWithBroker();
        using var _e = env;
        static JsonObject Review(string message) => new() { ["kind"] = "review", ["message"] = message };
        await server.HandleAsync(id, Call("wait_for_user", Review("The form is filled in; please review it.")), default);
        var requestId = store.Read(id, r => r.PendingHelp!.Id)!;

        // The CLI gave up on the call (its tool timeout) - the model never saw the request id in a reply.
        var aborted = await server.HandleAsync(id, Call("wait_for_user", Review("The form is filled in; please review it.")), new CancellationToken(canceled: true));
        Assert.NotNull(aborted);

        // The user presses "Looks good - submit it" while the model is between two calls.
        Assert.NotNull(broker.Resolve(id, requestId, new HelpResponse("approve", "")));

        // The model asks again, from scratch and in different words.
        var reply = await server.HandleAsync(id, Call("wait_for_user", Review("Still waiting for your review of the form.")), default);

        Assert.Contains("APPROVES", Text(reply));
        Assert.Null(store.Read(id, r => r.PendingHelp)); // no second review card for the user to deal with
        Assert.False(broker.IsWaiting(id));
    }

    [Fact]
    public async Task A_confirm_request_returns_a_plain_yes_to_the_model()
    {
        var (server, store, _, _, id, env, broker) = CreateWithBroker();
        using var _e = env;
        await server.HandleAsync(id, Call("wait_for_user", new JsonObject { ["kind"] = "confirm", ["message"] = "Apply anyway?" }), default);
        var pending = store.Read(id, r => r.PendingHelp)!;
        Assert.Equal(HelpKind.Confirm, pending.Kind);

        broker.Resolve(id, pending.Id, new HelpResponse("yes", ""));
        var response = await server.HandleAsync(id, Call("wait_for_user", new JsonObject { ["kind"] = "confirm", ["message"] = "", ["request_id"] = pending.Id }), default);

        Assert.Equal("The user said YES: go ahead.", Text(response));
    }

    [Fact]
    public void The_tool_description_lists_every_help_kind()
    {
        var kinds = ((JsonArray)JobMcpServer.ListTools()[0]!["inputSchema"]!["properties"]!["kind"]!["enum"]!).Select(k => (string)k!).ToArray();
        Assert.Equal(new[] { "captcha", "verification", "question", "confirm", "review", "other" }, kinds);
    }

    [Fact]
    public async Task report_progress_writes_to_the_job_log()
    {
        var (server, store, _, _, id, env) = Create();
        using var _e = env;

        await server.HandleAsync(id, Call("report_progress", new JsonObject { ["message"] = "Found it on Greenhouse" }), default);

        Assert.Contains(store.Read(id, r => r.Log.ToList())!, l => l.Kind == "agent" && l.Text == "Found it on Greenhouse");
    }
}
