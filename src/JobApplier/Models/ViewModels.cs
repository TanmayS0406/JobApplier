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

public sealed class LiveViewModel
{
    /// <summary>Set while new jobs are held back because the AI usage limit was reached.</summary>
    public DateTime? PausedUntilUtc { get; init; }

    public IReadOnlyList<JobRow> Rows { get; init; } = Array.Empty<JobRow>();
    public IReadOnlyList<HelpItem> Help { get; init; } = Array.Empty<HelpItem>();

    /// <summary>What the employer's mail says about each job (by job id), for the chip in the results table. Empty when Gmail is not used.</summary>
    public IReadOnlyDictionary<Guid, EmployerReply> Replies { get; init; } = new Dictionary<Guid, EmployerReply>();
}

/// <summary>The "Replies from employers" card on the dashboard.</summary>
public sealed class RepliesViewModel
{
    public bool HasClient { get; init; }
    public bool Connected { get; init; }
    public bool NeedsReconnect { get; init; }
    public string Email { get; init; } = "";
    public bool Checking { get; init; }
    public int ApplicationCount { get; init; }
    public DateTime? LastCheckedUtc { get; init; }
    public string LastError { get; init; } = "";

    /// <summary>Newest first.</summary>
    public IReadOnlyList<EmployerReply> Replies { get; init; } = Array.Empty<EmployerReply>();

    public int Count(ReplyKind kind) => Replies.Count(r => r.Kind == kind);
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
