using System.ComponentModel;
using System.Text.Json.Serialization;

namespace JobApplier.Domain;

public enum JobStatus
{
    Queued,
    Extracting,
    Searching,
    Matching,
    Applying,
    Applied,
    Skipped,
    NotFound,
    Failed,
    Cancelled
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Applied or JobStatus.Skipped or JobStatus.NotFound or JobStatus.Failed or JobStatus.Cancelled;

    public static string Label(this JobStatus status) => status switch
    {
        JobStatus.Queued => "Queued",
        JobStatus.Extracting => "Reading screenshot",
        JobStatus.Searching => "Finding the real posting",
        JobStatus.Matching => "Scoring against resume",
        JobStatus.Applying => "Filling the application",
        JobStatus.Applied => "Applied",
        JobStatus.Skipped => "Skipped",
        JobStatus.NotFound => "Posting not found",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };
}

/// <summary>Per-batch choices made on the upload form.</summary>
public sealed class JobOptions
{
    /// <summary>When false the agent fills everything, then waits for the user to approve the final Submit click.</summary>
    public bool AutoSubmit { get; set; }

    /// <summary>Jobs scoring below this percentage are not applied to. 0 disables the gate.</summary>
    public int MinMatchPercent { get; set; }
}

// ---- Structured results returned by the agents. [Description] text becomes part of the JSON schema. ----

public sealed class ExtractedJob
{
    [Description("Company name exactly as shown. Empty string if not visible.")]
    public string Company { get; set; } = "";

    [Description("Job title exactly as shown.")]
    public string Title { get; set; } = "";

    [Description("City / region / country as shown. Empty string if not visible.")]
    public string Location { get; set; } = "";

    [Description("One of: Remote, Hybrid, On-site, Unknown.")]
    public string WorkMode { get; set; } = "Unknown";

    [Description("Full-time, Contract, Internship, etc. Empty string if not visible.")]
    public string EmploymentType { get; set; } = "";

    [Description("Salary or pay range if shown, otherwise empty string.")]
    public string Salary { get; set; } = "";

    [Description("Key skills, tools and technologies named in the posting.")]
    public List<string> Skills { get; set; } = new();

    [Description("Every part of the job description that is readable in the screenshot, as plain text. Do not invent text that is not visible.")]
    public string Description { get; set; } = "";

    [Description("Anything useful for locating the real posting: recruiter name, job id, 'Easy Apply', posted date, applicant count.")]
    public string Hints { get; set; } = "";
}

/// <summary>What the extractor returns for one screenshot: a single posting, or one entry per card when it shows a list of jobs.</summary>
public sealed class ExtractedScreenshot
{
    [Description("Every distinct job visible in the screenshot, top to bottom. One entry when it shows a single posting, one per job card when it shows a list of results. Empty if no job is readable.")]
    public List<ExtractedJob> Jobs { get; set; } = new();
}

public sealed class FoundPosting
{
    [Description("True only if you found the same role on the company's own careers site or an ATS page (Greenhouse, Lever, Workday, Ashby, SmartRecruiters, ...).")]
    public bool Found { get; set; }

    [Description("URL of the page where one can apply. Empty string if not found.")]
    public string Url { get; set; } = "";

    [Description("Name of the portal, e.g. 'Greenhouse', 'Workday', 'Company careers site'.")]
    public string Portal { get; set; } = "";

    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";

    [Description("The complete job description text read from the page (responsibilities, requirements, benefits). Plain text, at most about 8000 characters.")]
    public string Description { get; set; } = "";

    [Description("One of: high, medium, low - how sure you are that this is the same role as in the screenshot.")]
    public string Confidence { get; set; } = "low";

    [Description("Short note: how it was found, or why it could not be found.")]
    public string Notes { get; set; } = "";
}

public sealed class MatchResult
{
    [Description("0-100: how well the candidate's skills and tools cover those the job requires.")]
    public int SkillsScore { get; set; }

    [Description("0-100: years and depth of relevant experience versus what the job asks for.")]
    public int ExperienceScore { get; set; }

    [Description("0-100: relevance of the candidate's industry / domain / type of work.")]
    public int DomainScore { get; set; }

    [Description("0-100: does the seniority of the role fit the candidate's level.")]
    public int SeniorityScore { get; set; }

    [Description("0-100: location, work mode, work authorisation and other logistics.")]
    public int LogisticsScore { get; set; }

    [Description("Two or three sentences: why this score.")]
    public string Summary { get; set; } = "";

    [Description("Skills / requirements from the job that the resume clearly shows.")]
    public List<string> MatchedSkills { get; set; } = new();

    [Description("Skills / requirements from the job that the resume does not show.")]
    public List<string> MissingSkills { get; set; } = new();

    /// <summary>Weighted overall percentage, computed here so the score is stable and explainable.</summary>
    [JsonIgnore]
    public int Percent => ComputePercent(SkillsScore, ExperienceScore, DomainScore, SeniorityScore, LogisticsScore);

    public static int ComputePercent(int skills, int experience, int domain, int seniority, int logistics)
    {
        static int Clamp(int v) => Math.Clamp(v, 0, 100);
        var weighted = Clamp(skills) * 40 + Clamp(experience) * 25 + Clamp(domain) * 15 + Clamp(seniority) * 10 + Clamp(logistics) * 10;
        return (int)Math.Round(weighted / 100.0, MidpointRounding.AwayFromZero);
    }
}

public enum ApplyResultKind
{
    Submitted,
    AlreadyApplied,
    Skipped,
    Blocked,
    Failed
}

public sealed class ApplyOutcome
{
    [Description("Submitted: the site confirmed the application. AlreadyApplied: the site says it was applied before. Skipped: the user chose not to submit. Blocked: could not continue (closed posting, needs something only the user has). Failed: something went wrong.")]
    public ApplyResultKind Result { get; set; } = ApplyResultKind.Failed;

    [Description("One or two sentences describing what happened.")]
    public string Summary { get; set; } = "";

    [Description("The confirmation text or reference number shown by the site, if any.")]
    public string Confirmation { get; set; } = "";
}

// ---- Job record ----

public sealed class LogEntry
{
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    /// <summary>info | agent | tool | warn | error | help</summary>
    public string Kind { get; set; } = "info";

    public string Text { get; set; } = "";
}

public enum HelpKind
{
    Captcha,
    Verification,

    /// <summary>A question on the application form itself. The answer is saved and reused for later applications.</summary>
    Question,
    Review,

    /// <summary>A one-off yes/no decision that is not a form question (e.g. "the page title differs, apply anyway?"). Never saved.</summary>
    Confirm,
    Other
}

public sealed class HelpRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public HelpKind Kind { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>File name of the uploaded screenshot inside the job folder.</summary>
    public string ScreenshotFile { get; set; } = "";
    public string OriginalFileName { get; set; } = "";

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string StatusDetail { get; set; } = "";
    public JobOptions Options { get; set; } = new();

    public ExtractedJob? Extracted { get; set; }
    public FoundPosting? Found { get; set; }
    public MatchResult? Match { get; set; }
    public ApplyOutcome? Apply { get; set; }

    public HelpRequest? PendingHelp { get; set; }
    public bool AccountRecorded { get; set; }
    public List<LogEntry> Log { get; set; } = new();

    // ---- usage limit: the AI ran out of tokens / quota, which waiting fixes ----

    /// <summary>The job failed because a token, quota or rate limit was reached (see <see cref="Infrastructure.UsageLimit"/>).</summary>
    public bool LimitHit { get; set; }

    /// <summary>The earliest sensible time to retry: the reset time the error gave, or an estimate when it did not say.</summary>
    public DateTime? RetryAfterUtc { get; set; }

    /// <summary>True when <see cref="RetryAfterUtc"/> came from the error itself rather than being estimated.</summary>
    public bool LimitResetKnown { get; set; }

    /// <summary>The user asked for the job to be retried automatically once the limit has reset.</summary>
    public bool AutoRetry { get; set; }

    /// <summary>How many automatic retries have been made for this limit (bounded, so a long outage cannot loop forever).</summary>
    public int LimitRetries { get; set; }

    /// <summary>Failed on a usage limit and armed to retry by itself: it is waiting, not abandoned.</summary>
    [JsonIgnore]
    public bool WaitingForLimitReset => Status == JobStatus.Failed && LimitHit && AutoRetry;

    /// <summary>
    /// Password generated for any account this job creates. Kept in memory only: it reaches the browser through
    /// Playwright's secrets file and the accounts document, never the model or the job log.
    /// </summary>
    [JsonIgnore]
    public string? Password { get; set; }

    [JsonIgnore]
    public string DisplayCompany => FirstNonEmpty(Found?.Company, Extracted?.Company) ?? "(reading...)";

    [JsonIgnore]
    public string DisplayTitle => FirstNonEmpty(Found?.Title, Extracted?.Title) ?? "";

    [JsonIgnore]
    public string DisplayLocation => FirstNonEmpty(Found?.Location, Extracted?.Location) ?? "";

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

// ---- Profile, accounts, settings ----

public sealed class SavedAnswer
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
}

public sealed class UserProfile
{
    [Description("Full name as written on the resume.")]
    public string FullName { get; set; } = "";

    [Description("Primary e-mail address on the resume. Empty string if none.")]
    public string Email { get; set; } = "";

    public string Phone { get; set; } = "";

    [Description("Current city, region and country.")]
    public string Location { get; set; } = "";

    public string LinkedInUrl { get; set; } = "";
    public string GitHubUrl { get; set; } = "";
    public string PortfolioUrl { get; set; } = "";

    [Description("Current or most recent job title.")]
    public string CurrentTitle { get; set; } = "";

    [Description("Current or most recent employer.")]
    public string CurrentCompany { get; set; } = "";

    [Description("Total years of professional experience as a number, or empty string if unclear.")]
    public string YearsOfExperience { get; set; } = "";

    [Description("Two to four sentence professional summary written from the resume.")]
    public string Summary { get; set; } = "";

    public List<string> Skills { get; set; } = new();

    [Description("Highest education: degree, field, institution, year.")]
    public string Education { get; set; } = "";

    // Not on a resume - filled in by the user and reused by the form-filling agent.
    public string WorkAuthorization { get; set; } = "";
    public string NoticePeriod { get; set; } = "";
    public string ExpectedSalary { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>File name of the resume copy kept in the data folder.</summary>
    public string ResumeFileName { get; set; } = "";

    /// <summary>Plain-text resume, used for scoring and for form questions.</summary>
    public string ResumeText { get; set; } = "";

    /// <summary>Questions the user answered during earlier applications, reused automatically.</summary>
    public List<SavedAnswer> Answers { get; set; } = new();
}

public sealed class AccountRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Site { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A posting the app has applied to. Kept after the job is deleted, so the same position is never applied to twice.</summary>
public sealed class ApplicationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";

    /// <summary>Address of the page that was applied on. Two addresses that differ only in tracking parameters count as the same posting.</summary>
    public string Url { get; set; } = "";

    /// <summary>Submitted, or AlreadyApplied when the site said an earlier application existed.</summary>
    public ApplyResultKind Result { get; set; } = ApplyResultKind.Submitted;

    public DateTime AppliedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The command-line AI agents JobApplier can run its jobs on. Each is signed in with the user's own account.</summary>
public static class AiProvider
{
    public const string Claude = "claude";
    public const string Gemini = "gemini";

    public static string Normalize(string? provider) =>
        string.Equals(provider?.Trim(), Gemini, StringComparison.OrdinalIgnoreCase) ? Gemini : Claude;

    public static string Label(string? provider) => Normalize(provider) == Gemini ? "Gemini" : "Claude";
}

public sealed class AppSettings
{
    /// <summary>Which AI runs the jobs: <c>claude</c> (Claude Code CLI) or <c>gemini</c> (Gemini CLI).</summary>
    public string Provider { get; set; } = AiProvider.Claude;

    /// <summary>Full path to gemini (or gemini.cmd). Empty = find it automatically.</summary>
    public string GeminiPath { get; set; } = "";

    /// <summary>Gemini model id, for example <c>gemini-2.5-pro</c>. Empty = the Gemini CLI's default.</summary>
    public string GeminiModel { get; set; } = "";

    /// <summary>Full path to claude.exe. Empty = find it automatically.</summary>
    public string ClaudePath { get; set; } = "";

    /// <summary>Alias (opus, sonnet, haiku, fable) or full model id. Empty = Claude Code's default.</summary>
    public string Model { get; set; } = "sonnet";

    /// <summary>low | medium | high | xhigh | max. Empty = the model's default.</summary>
    public string Effort { get; set; } = "";

    /// <summary>How many jobs are processed at the same time (each one gets its own browser window).</summary>
    public int MaxParallelJobs { get; set; } = 3;

    /// <summary>chrome | msedge | firefox | webkit. Headed by default so captchas can be solved by hand.</summary>
    public string Browser { get; set; } = "chrome";
    public bool Headless { get; set; }

    public string PlaywrightMcpVersion { get; set; } = "0.0.85";

    public bool DefaultAutoSubmit { get; set; }
    public int DefaultMinMatchPercent { get; set; }

    /// <summary>Where the accounts document is written. Empty = JobApplier-Accounts.docx on the Desktop.</summary>
    public string AccountsDocumentPath { get; set; } = "";

    /// <summary>How long one job may wait for the user (captcha, question, review) before it gives up.</summary>
    public int HelpTimeoutMinutes { get; set; } = 45;

    /// <summary>Model that reads the mailbox for employer replies. A small one is plenty and keeps the usage cost low. Empty = Claude Code's default.</summary>
    public string MailModel { get; set; } = "haiku";

    /// <summary>How often, in hours, the mailbox is checked by itself once reply tracking is on. 0 = only when asked.</summary>
    public int MailAutoCheckHours { get; set; } = 6;
}
