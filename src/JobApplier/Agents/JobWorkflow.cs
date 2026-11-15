using JobApplier.Claude;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace JobApplier.Agents;

/// <summary>What a stage hands to the next one. The data itself lives on the <see cref="JobRecord"/>; this only carries the verdict.</summary>
public sealed record StageResult(Guid JobId, bool Continue);

/// <param name="Enqueue">
/// Queues a job on the runner. The Extract stage uses it for the extra jobs it finds in a screenshot that lists several.
/// A delegate rather than the runner itself because the runner is built from these services.
/// </param>
/// <param name="OnUsageLimit">
/// Told when a stage hit a token / quota limit and when it should be over (null = unknown), so the runner can hold back the
/// jobs that are still queued instead of letting each one fail the same way.
/// </param>
public sealed record PipelineServices(
    AgentFactory Agents,
    JobStore Store,
    ProfileService Profile,
    AccountService Accounts,
    AppPaths Paths,
    Func<Guid, bool>? Enqueue = null,
    Action<DateTime?>? OnUsageLimit = null);

/// <summary>
/// The per-job pipeline as a Microsoft Agent Framework workflow:
/// <c>Extract (screenshot) -> Find (real posting) -> Match (score) -> Apply (fill the form)</c>.
/// Each stage is an executor that runs a CLI-backed agent (Claude Code or Gemini, as selected in Settings); conditional edges stop the pipeline when a stage decides the job
/// should not continue (posting not found, match below the user's minimum, failure).
/// Stages are resumable: finished work is stored on the job, so a retry continues where it stopped.
/// </summary>
public static class JobWorkflow
{
    public static Workflow Build(PipelineServices services)
    {
        var extract = new ExtractExecutor(services);
        var find = new FindExecutor(services);
        var match = new MatchExecutor(services);
        var apply = new ApplyExecutor(services);

        return new WorkflowBuilder(extract)
            .AddEdge<StageResult>(extract, find, r => r is { Continue: true })
            .AddEdge<StageResult>(find, match, r => r is { Continue: true })
            .AddEdge<StageResult>(match, apply, r => r is { Continue: true })
            .WithName("job-application")
            .WithOutputFrom(apply)
            .Build();
    }

    /// <summary>Runs the whole pipeline for one job and returns when it has finished (successfully or not).</summary>
    public static async Task RunAsync(Workflow workflow, Guid jobId, CancellationToken cancellationToken)
    {
        // Every stage consumes the previous stage's StageResult, so the run starts with one too. (A message whose type
        // matches no executor's input is dropped silently by the engine - see WorkflowSemanticsTests.)
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, new StageResult(jobId, true), cancellationToken: cancellationToken);
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            // Stage executors report their own outcome on the job; this loop just drives the workflow to completion.
            if (evt is ExecutorFailedEvent failed)
                throw new InvalidOperationException($"Stage '{failed.ExecutorId}' failed: {failed.Data}");
        }
    }
}

/// <summary>Shared error handling: every stage ends the job in a clear state instead of throwing into the workflow engine.</summary>
internal abstract class StageExecutor : Executor<StageResult, StageResult>
{
    protected StageExecutor(string id, PipelineServices services) : base(id) => Services = services;

    protected PipelineServices Services { get; }
    protected JobStore Store => Services.Store;

    public override async ValueTask<StageResult> HandleAsync(StageResult previous, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var jobId = previous.JobId;
        try
        {
            return new StageResult(jobId, await RunStageAsync(jobId, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Store.SetStatus(jobId, JobStatus.Cancelled, "Cancelled.");
            Store.Log(jobId, "info", "Cancelled.");
        }
        catch (ClaudeAgentException ex) when (ex.IsUsageLimit)
        {
            FailForLimit(jobId, ex);
        }
        catch (ClaudeAgentException ex)
        {
            Fail(jobId, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(jobId, "Unexpected error: " + ex.Message);
        }
        return new StageResult(jobId, false);
    }

    /// <summary>Returns true when the next stage should run.</summary>
    protected abstract Task<bool> RunStageAsync(Guid jobId, CancellationToken cancellationToken);

    protected void Fail(Guid jobId, string message)
    {
        Store.Update(jobId, r => { r.LimitHit = false; r.RetryAfterUtc = null; });
        Store.SetStatus(jobId, JobStatus.Failed, message);
        Store.Log(jobId, "error", message);
    }

    /// <summary>
    /// The AI ran out of tokens or quota. Nothing is wrong with the job itself, so it is marked as waiting for the limit to reset:
    /// the dashboard offers "Retry when available" and the runner holds back the jobs that have not started yet.
    /// </summary>
    internal void FailForLimit(Guid jobId, ClaudeAgentException ex, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var retries = Store.Read(jobId, r => r.LimitRetries);
        var known = ex.RetryAfterUtc != null;
        var retryAfter = ex.RetryAfterUtc ?? now + UsageLimit.Backoff(retries); // not told: probe again after a growing pause

        var detail = "The AI usage limit was reached, so this job stopped. " +
                     (known ? "It should reset " : "No reset time was given; it will be tried again ") + UsageLimit.Describe(retryAfter, now) + ". " +
                     "Nothing is lost: press Retry once it has reset, or Retry when available to let JobApplier do it." +
                     (Id == "apply" ? " The application may already have been submitted before this happened - check the employer's site before retrying." : "");

        Store.Update(jobId, r => { r.LimitHit = true; r.RetryAfterUtc = retryAfter; r.LimitResetKnown = known; });
        Store.SetStatus(jobId, JobStatus.Failed, detail);
        Store.Log(jobId, "warn", "Usage limit reached: " + Clip(ex.Message, 300));
        Services.OnUsageLimit?.Invoke(retryAfter);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    protected static async Task<T> RunTypedAsync<T>(CliAgent agent, string prompt, CancellationToken cancellationToken)
    {
        var response = await agent.RunAsync<T>(prompt, session: null, serializerOptions: JsonFile.Options, options: null, cancellationToken: cancellationToken);
        return response.Result;
    }
}

/// <summary>
/// Stage 1: read the LinkedIn screenshot. A screenshot may list several jobs: the first stays with this job and every other one
/// becomes a job of its own (same image, details already filled in), queued at once so they run in parallel like any other.
/// </summary>
internal sealed class ExtractExecutor : StageExecutor
{
    public ExtractExecutor(PipelineServices services) : base("extract", services) { }

    protected override async Task<bool> RunStageAsync(Guid jobId, CancellationToken cancellationToken)
    {
        // A resumed run, or a job that was split off a list and already carries its details.
        if (Store.Read(jobId, r => r.Extracted) != null) return true;

        Store.SetStatus(jobId, JobStatus.Extracting, "Reading the screenshot");
        var screenshot = Store.Read(jobId, r => r.ScreenshotFile)!;

        // The screenshot is copied into the workspace: the agent's Read tool cannot leave it.
        var workspace = Services.Paths.EnsureWorkspace(jobId);
        File.Copy(Path.Combine(Services.Paths.JobFolder(jobId), screenshot), Path.Combine(workspace, screenshot), overwrite: true);

        var agent = Services.Agents.CreateForJob(jobId, "extractor", Prompts.ExtractInstructions, AgentCapabilities.ReadFiles);
        var read = await RunTypedAsync<ExtractedScreenshot>(agent, Prompts.ExtractPrompt(screenshot), cancellationToken);
        var jobs = DistinctJobs(read.Jobs);

        if (jobs.Count == 0)
        {
            Fail(jobId, "Could not read a job posting in this screenshot. Is it a clear screenshot of the posting?");
            return false;
        }

        var job = jobs[0];
        Store.Update(jobId, r => r.Extracted = job);
        Store.Log(jobId, "info", $"Read the screenshot: {Describe(job)}");

        if (jobs.Count > 1)
        {
            Store.Log(jobId, "info", $"The screenshot lists {jobs.Count} jobs. This job takes the first; each of the other {jobs.Count - 1} becomes a job of its own and runs in parallel.");
            SplitOff(jobId, jobs.Skip(1).ToList(), jobs.Count);
        }
        return true;
    }

    /// <summary>
    /// The readable jobs, without repeats. A job counts when its title or company was read; two entries with the same company,
    /// title and location are one job (a list next to a details pane shows the selected job twice).
    /// </summary>
    internal static List<ExtractedJob> DistinctJobs(IEnumerable<ExtractedJob>? jobs)
    {
        var seen = new HashSet<string>();
        var result = new List<ExtractedJob>();
        foreach (var job in jobs ?? Enumerable.Empty<ExtractedJob>())
        {
            if (string.IsNullOrWhiteSpace(job.Company) && string.IsNullOrWhiteSpace(job.Title)) continue;
            if (seen.Add($"{Normalise(job.Company)}|{Normalise(job.Title)}|{Normalise(job.Location)}")) result.Add(job);
        }
        return result;
    }

    private static string Normalise(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string Describe(ExtractedJob job) =>
        $"{job.Title} at {job.Company}" + (job.Location.Length > 0 ? $" ({job.Location})" : "");

    /// <summary>
    /// Creates one job per extra posting, each with its own copy of the screenshot so it can be viewed and deleted on its own.
    /// One that cannot be created is reported on this job and does not stop the others, or this job.
    /// </summary>
    internal void SplitOff(Guid parentId, IReadOnlyList<ExtractedJob> others, int total)
    {
        var parent = Store.Read(parentId, r => (r.ScreenshotFile, r.OriginalFileName, r.Options.AutoSubmit, r.Options.MinMatchPercent));
        var source = Path.Combine(Services.Paths.JobFolder(parentId), parent.ScreenshotFile);

        foreach (var other in others)
        {
            try
            {
                var child = new JobRecord
                {
                    OriginalFileName = parent.OriginalFileName,
                    ScreenshotFile = parent.ScreenshotFile,
                    Options = new JobOptions { AutoSubmit = parent.AutoSubmit, MinMatchPercent = parent.MinMatchPercent },
                    Extracted = other
                };
                child.Log.Add(new LogEntry { Kind = "info", Text = $"One of {total} jobs listed in {parent.OriginalFileName}: {Describe(other)}" });

                Directory.CreateDirectory(Services.Paths.JobFolder(child.Id));
                File.Copy(source, Path.Combine(Services.Paths.JobFolder(child.Id), child.ScreenshotFile));
                Store.Add(child);
                Services.Enqueue?.Invoke(child.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Store.Log(parentId, "warn", $"Could not start a job for {Describe(other)}: {ex.Message}");
            }
        }
    }
}

/// <summary>Stage 2: find the same role on the employer's own site and read the full description.</summary>
internal sealed class FindExecutor : StageExecutor
{
    public FindExecutor(PipelineServices services) : base("find", services) { }

    protected override async Task<bool> RunStageAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (Store.Read(jobId, r => r.Found) != null) return true;

        Store.SetStatus(jobId, JobStatus.Searching, "Searching for the real posting");
        var extracted = Store.Read(jobId, r => r.Extracted)!;

        var agent = Services.Agents.CreateForJob(jobId, "finder", Prompts.FindInstructions, AgentCapabilities.Browser | AgentCapabilities.AppTools);
        var found = await RunTypedAsync<FoundPosting>(agent, Prompts.FindPrompt(extracted, Services.Profile.Current.Location), cancellationToken);

        if (found.Found && !IsWebUrl(found.Url))
        {
            found.Found = false;
            found.Notes = (found.Notes + " (The address returned was not a web page.)").Trim();
        }

        Store.Update(jobId, r => r.Found = found);
        Store.Log(jobId, "info", found.Found
            ? $"Found the posting on {(found.Portal.Length > 0 ? found.Portal : "the company site")}: {found.Url}"
            : "Could not find the posting on the company's site. " + found.Notes);
        return true; // scoring still runs, from the screenshot text
    }

    internal static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>Stage 3: score the job against the resume and apply the user's minimum.</summary>
internal sealed class MatchExecutor : StageExecutor
{
    public MatchExecutor(PipelineServices services) : base("match", services) { }

    protected override async Task<bool> RunStageAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var profile = Services.Profile.Current;
        if (string.IsNullOrWhiteSpace(profile.ResumeText))
        {
            Fail(jobId, "No resume yet. Add it on the Profile page, then press Retry.");
            return false;
        }

        if (Store.Read(jobId, r => r.Match) == null)
        {
            Store.SetStatus(jobId, JobStatus.Matching, "Comparing the job with your resume");
            var (title, company, description) = Store.Read(jobId, r =>
            {
                var useFound = r.Found is { Found: true } && r.Found.Description.Length > 200;
                return (
                    r.DisplayTitle,
                    r.DisplayCompany,
                    useFound ? r.Found!.Description : r.Extracted!.Description);
            });

            var agent = Services.Agents.CreateForJob(jobId, "matcher", Prompts.MatchInstructions, AgentCapabilities.None);
            var match = await RunTypedAsync<MatchResult>(agent, Prompts.MatchPrompt(profile, title, company, description), cancellationToken);
            Store.Update(jobId, r => r.Match = match);
            Store.Log(jobId, "info", $"Match with your resume: {match.Percent}%. {match.Summary}");
        }

        var state = Store.Read(jobId, r => (r.Match!.Percent, r.Found!.Found, r.Options.MinMatchPercent))!;
        if (!state.Found)
        {
            Store.SetStatus(jobId, JobStatus.NotFound,
                "The real posting was not found on the company's site, so nothing was applied. The match is scored from the screenshot.");
            return false;
        }
        if (state.MinMatchPercent > 0 && state.Percent < state.MinMatchPercent)
        {
            Store.SetStatus(jobId, JobStatus.Skipped, $"Match {state.Percent}% is below your minimum of {state.MinMatchPercent}%.");
            Store.Log(jobId, "info", $"Skipped: {state.Percent}% < {state.MinMatchPercent}%.");
            return false;
        }
        return true;
    }
}

/// <summary>Stage 4: create an account if needed and fill (and, if allowed, submit) the application.</summary>
internal sealed class ApplyExecutor : StageExecutor
{
    private static readonly object ApplyGate = new();

    public ApplyExecutor(PipelineServices services) : base("apply", services) { }

    protected override async Task<bool> RunStageAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var profile = Services.Profile.Current;
        if (string.IsNullOrWhiteSpace(profile.Email))
        {
            Fail(jobId, "Your profile has no e-mail address. Add it on the Profile page, then press Retry.");
            return false;
        }
        if (!Services.Profile.HasResume)
        {
            Fail(jobId, "The resume file is missing. Upload it again on the Profile page, then press Retry.");
            return false;
        }

        var posting = Store.Read(jobId, r => r.Found)!;

        // Claim the posting. Check and claim happen under one lock so two parallel jobs for the same posting cannot both pass.
        lock (ApplyGate)
        {
            // The applications list outlives jobs, so this also catches a posting whose earlier job was deleted.
            var earlier = Services.Accounts.FindApplication(jobId, posting.Url);
            if (earlier != null)
            {
                var when = earlier.AppliedUtc.ToLocalTime().ToString("yyyy-MM-dd");
                Store.SetStatus(jobId, JobStatus.Skipped,
                    $"Already applied to this posting on {when} ({earlier.Role} at {earlier.Company}); nothing was submitted twice.");
                Store.Log(jobId, "info", $"Skipped: {posting.Url} is in the applications list (applied {when}).");
                return false;
            }

            var duplicate = PostingKey.FindDuplicate(jobId, posting.Url,
                Store.ReadAll(r => (r.Id, r.Found?.Url, r.Status)));
            if (duplicate != null)
            {
                var title = Store.Read(duplicate.Value, r => r.DisplayTitle + " at " + r.DisplayCompany) ?? "another job";
                Store.SetStatus(jobId, JobStatus.Skipped, $"This posting is already handled by another job ({title}); nothing was submitted twice.");
                Store.Log(jobId, "info", "Skipped: the same posting has already been applied to (or is being applied to) by another job.");
                return false;
            }
            Store.SetStatus(jobId, JobStatus.Applying, "Filling in the application");
        }

        // A strong, unique password per site. If an earlier job already created an account on this site, that account's
        // password is reused: the site would reject a new one for an e-mail that is already registered.
        // It lives in memory only; the browser receives it through Playwright's secrets file.
        var existingAccount = Services.Accounts.FindForSite(posting.Url, profile.Email);
        Store.Update(jobId, r => r.Password ??= existingAccount?.Password ?? PasswordGenerator.Generate());
        if (existingAccount != null)
            Store.Log(jobId, "info", $"An account for {AccountService.HostOf(posting.Url)} already exists from an earlier job; signing in with it.");

        var workspace = Services.Paths.EnsureWorkspace(jobId);
        var resumeCopy = CopyResume(workspace);

        var autoSubmit = Store.Read(jobId, r => r.Options.AutoSubmit);

        var passwordTyped = false;
        var agent = Services.Agents.CreateForJob(
            jobId, "applicant", Prompts.ApplyInstructions(autoSubmit),
            AgentCapabilities.Browser | AgentCapabilities.AppTools | AgentCapabilities.PasswordSecret,
            evt => { if (evt is ToolUseEvent tool && TypesPassword(tool)) passwordTyped = true; });

        ApplyOutcome outcome;
        try
        {
            outcome = await RunTypedAsync<ApplyOutcome>(
                agent, Prompts.ApplyPrompt(posting, profile, resumeCopy, accountAlreadyExists: existingAccount != null), cancellationToken);
        }
        finally
        {
            // The secrets file only needs to exist while the browser runs.
            try { File.Delete(Path.Combine(Services.Paths.PrivateFolder(jobId), "secrets.env")); } catch (IOException) { /* removed with the job */ }
        }

        // Safety net: the password was typed somewhere but the agent never called record_account -> do not lose it.
        if (passwordTyped && !Store.Read(jobId, r => r.AccountRecorded))
        {
            var site = AccountService.HostOf(posting.Url);
            Services.Accounts.Add(new AccountRecord
            {
                JobId = jobId,
                Company = Store.Read(jobId, r => r.DisplayCompany) ?? posting.Company,
                Role = posting.Title,
                Site = site,
                Email = profile.Email,
                Password = Store.Read(jobId, r => r.Password)!,
                Note = "Saved automatically: the password was entered on the site but account creation was not confirmed."
            });
            Store.Update(jobId, r => r.AccountRecorded = true);
            Store.Log(jobId, "warn", "The password was used on the site, so it was saved to the accounts document just in case.");
        }

        Store.Update(jobId, r => r.Apply = outcome);
        var summary = string.IsNullOrWhiteSpace(outcome.Summary) ? outcome.Result.ToString() : outcome.Summary;
        switch (outcome.Result)
        {
            case ApplyResultKind.Submitted:
                Store.SetStatus(jobId, JobStatus.Applied, summary);
                break;
            case ApplyResultKind.AlreadyApplied:
                Store.SetStatus(jobId, JobStatus.Applied, "Already applied earlier. " + summary);
                break;
            case ApplyResultKind.Skipped:
                Store.SetStatus(jobId, JobStatus.Skipped, "You skipped this job. " + summary);
                break;
            case ApplyResultKind.Blocked:
                Store.SetStatus(jobId, JobStatus.Failed, "Blocked: " + summary);
                break;
            default:
                Store.SetStatus(jobId, JobStatus.Failed, summary);
                break;
        }
        Store.Log(jobId, "info", $"Result: {outcome.Result}. {summary}");

        if (outcome.Result is ApplyResultKind.Submitted or ApplyResultKind.AlreadyApplied)
            RecordApplication(jobId, posting, outcome.Result);
        return false;
    }

    /// <summary>
    /// Adds the posting to the applications list and the Desktop document. This runs after the job is marked Applied, so a
    /// failure here (disk full, file locked) is only logged: it must not turn a submitted application into a "Failed" job
    /// that the user would retry.
    /// </summary>
    private void RecordApplication(Guid jobId, FoundPosting posting, ApplyResultKind result)
    {
        try
        {
            var (company, role) = Store.Read(jobId, r => (r.DisplayCompany, r.DisplayTitle));
            Services.Accounts.RecordApplication(new ApplicationRecord
            {
                JobId = jobId, Company = company, Role = role, Url = posting.Url, Result = result
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Store.Log(jobId, "warn", "Applied, but the posting could not be saved to the applications list: " + ex.Message);
        }
    }

    /// <summary>
    /// Puts the user's current resume in the job's workspace, under the name the user gave it, and returns its path. It is the
    /// only file in that folder: a copy left by an earlier run of this job (before the resume was replaced) is removed, so the
    /// agent cannot pick the wrong one.
    /// </summary>
    internal string CopyResume(string workspace)
    {
        var folder = Path.Combine(workspace, "resume");
        Directory.CreateDirectory(folder);

        var source = Services.Profile.ResumePath;
        var target = Path.Combine(folder, Path.GetFileName(source));
        foreach (var old in Directory.GetFiles(folder).Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(old); } catch (IOException) { /* still open somewhere; the agent is given the exact path anyway */ }
        }

        File.Copy(source, target, overwrite: true);
        return target;
    }

    /// <summary>True when a tool call entered the JOB_PASSWORD secret into a page.</summary>
    internal static bool TypesPassword(ToolUseEvent tool)
    {
        if (tool.Input == null) return false;
        if (tool.Name.EndsWith("browser_type", StringComparison.Ordinal))
            return (string?)tool.Input["text"] == Prompts.PasswordSecretName;
        if (tool.Name.EndsWith("browser_fill_form", StringComparison.Ordinal) && tool.Input["fields"] is System.Text.Json.Nodes.JsonArray fields)
            return fields.OfType<System.Text.Json.Nodes.JsonObject>().Any(f => (string?)f["value"] == Prompts.PasswordSecretName);
        return false;
    }
}
