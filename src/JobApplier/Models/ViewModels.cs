using JobApplier.Domain;
using JobApplier.Mail;

namespace JobApplier.Models;

/// <summary>One line of the results table. Built under the job's lock, so it is a safe snapshot.</summary>
public sealed record JobRow(
    Guid Id,
    string Company,
    string Title,
    string Location,
    JobStatus Status,
    string StatusDetail,
    int? MatchPercent,
    string? PostingUrl,
    string? Portal,
    bool PostingFound,
    bool NeedsUser,
    bool AccountRecorded,
    DateTime CreatedUtc,
    bool LimitHit = false,
    bool AutoRetry = false,
    DateTime? RetryAfterUtc = null)
{
    public static JobRow From(JobRecord r) => new(
        r.Id,
        r.DisplayCompany,
        r.DisplayTitle,
        r.DisplayLocation,
        r.Status,
        r.StatusDetail,
        r.Match?.Percent,
        r.Found is { Found: true } ? r.Found.Url : null,
        r.Found is { Found: true } ? r.Found.Portal : null,
        r.Found is { Found: true },
        r.PendingHelp != null,
        r.AccountRecorded,
        r.CreatedUtc,
        r.LimitHit,
        r.AutoRetry,
        r.RetryAfterUtc);

    public string MatchBand => MatchPercent switch
    {
        null => "none",
        >= 75 => "high",
        >= 50 => "mid",
        _ => "low"
    };
}

public sealed record HelpItem(Guid JobId, string Company, string Title, HelpRequest Request);

/// <summary>
/// What the results header and its buttons say about ALL jobs, however many there are on the page and whatever filter is set:
/// "Retry all failed (24)" must count the 24, not the 10 that happen to be shown.
/// </summary>
public sealed record JobCounts(int Total, int Running, int Applied, int Waiting, int Limited, int FailedAll)
{
    /// <summary>A failed job that is waiting for a usage limit to reset is not abandoned, so it is not counted as one to clear.</summary>
    public int Failed => FailedAll - Waiting;

    public static JobCounts From(IReadOnlyList<JobRow> rows) => new(
        rows.Count,
        rows.Count(r => !r.Status.IsTerminal()),
        rows.Count(r => r.Status == JobStatus.Applied),
        rows.Count(r => r.Status == JobStatus.Failed && r.LimitHit && r.AutoRetry),
        rows.Count(r => r.Status == JobStatus.Failed && r.LimitHit && !r.AutoRetry),
        rows.Count(r => r.Status == JobStatus.Failed));
}

public sealed class LiveViewModel
{
    /// <summary>Set while new jobs are held back because the AI usage limit was reached.</summary>
    public DateTime? PausedUntilUtc { get; init; }

    /// <summary>The jobs on the current page, after the filter and the sort.</summary>
    public IReadOnlyList<JobRow> Rows { get; init; } = Array.Empty<JobRow>();

    /// <summary>Totals over every job (not just this page).</summary>
    public JobCounts Counts { get; init; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>The paging, sort and filter in effect (the page already corrected to one that exists).</summary>
    public ListQuery Query { get; init; } = ListQuery.Default;

    public PageInfo Page { get; init; } = new(1, 10, 0, 1, 0, 0);
    public IReadOnlyList<HelpItem> Help { get; init; } = Array.Empty<HelpItem>();

    /// <summary>Reply tracking is on, so a job without a reply is "no reply yet" rather than unknown.</summary>
    public bool RepliesTracked { get; init; }

    /// <summary>What the employer's mail says about each job (by job id), for the Employer reply column of the results table. Empty when tracking is off.</summary>
    public IReadOnlyDictionary<Guid, EmployerReply> Replies { get; init; } = new Dictionary<Guid, EmployerReply>();
}

/// <summary>The "Replies from employers" card on the dashboard.</summary>
public sealed class RepliesViewModel
{
    /// <summary>Reply tracking is turned on (Settings). Nothing reads the mailbox before that.</summary>
    public bool Enabled { get; init; }

    public bool Checking { get; init; }
    public int ApplicationCount { get; init; }
    public DateTime? LastCheckedUtc { get; init; }
    public string LastError { get; init; } = "";

    /// <summary>How many replies exist, before any filter.</summary>
    public int Total { get; init; }

    /// <summary>Replies per kind over all replies (the chips above the list), not just this page.</summary>
    public IReadOnlyDictionary<ReplyKind, int> KindCounts { get; init; } = new Dictionary<ReplyKind, int>();

    /// <summary>The match percentage of each reply's job, for the sort and the chip on a reply. A job that was never scored, or was deleted, has none.</summary>
    public IReadOnlyDictionary<Guid, int> MatchByJob { get; init; } = new Dictionary<Guid, int>();

    public ListQuery Query { get; init; } = ListQuery.Default;
    public PageInfo Page { get; init; } = new(1, 10, 0, 1, 0, 0);

    /// <summary>Newest first.</summary>
    public IReadOnlyList<EmployerReply> Replies { get; init; } = Array.Empty<EmployerReply>();

    public int Count(ReplyKind kind) => KindCounts.GetValueOrDefault(kind);
}

public sealed class DashboardViewModel
{
    public required LiveViewModel Live { get; init; }
    public required RepliesViewModel Replies { get; init; }
    public bool HasResume { get; init; }
    public bool HasEmail { get; init; }
    public string ProfileEmail { get; init; } = "";
    public bool DefaultAutoSubmit { get; init; }
    public int DefaultMinMatch { get; init; }
    public bool DefaultRequireVisaOrRelocation { get; init; }
    public int MaxParallel { get; init; }
    public string AccountsDocumentPath { get; init; } = "";
}

public sealed class JobDetailsViewModel
{
    public required JobRecord Job { get; init; }
    public required string Company { get; init; }
    public required string Title { get; init; }
    public bool IsActive { get; init; }
    public string ProfileEmail { get; init; } = "";
    public string AccountsDocumentPath { get; init; } = "";
    public bool HasScreenshot { get; init; }
}

public sealed class ProfileViewModel
{
    public required UserProfile Profile { get; init; }
    public string SkillsText { get; set; } = "";
    public bool HasResume { get; init; }
}

public sealed class SettingsViewModel
{
    public required AppSettings Settings { get; init; }
    public string DataFolder { get; init; } = "";
    public string AccountsDocumentPath { get; init; } = "";
    public string? DocumentError { get; init; }
    public int AccountCount { get; init; }
    public int ApplicationCount { get; init; }
    public string Provider { get; init; } = AiProvider.Claude;
    public IReadOnlyList<SystemCheck> Checks { get; init; } = Array.Empty<SystemCheck>();
}

public sealed record SystemCheck(string Name, bool Ok, string Detail);

/// <summary>The starting values of the Retry dialog when the button that opens it does not carry its own (the dashboard buttons use the defaults of the upload form).</summary>
public sealed record RetryDialogModel(bool DefaultAutoSubmit, int DefaultMinMatch, bool DefaultRequireVisaOrRelocation = false);
