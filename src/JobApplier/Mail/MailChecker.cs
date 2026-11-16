using System.Globalization;
using System.Text;
using JobApplier.Domain;
using JobApplier.Services;

namespace JobApplier.Mail;

/// <summary>What one pass over the mailbox did. <paramref name="Message"/> is meant to be shown to the user as it is.</summary>
public sealed record MailCheckResult(bool Ok, string Message, int NewReplies);

/// <summary>
/// Has Claude look through the user's Gmail for answers to the applications this app has made, and keeps what it reports. Claude's
/// answer is treated as untrusted input: a reply is stored only if it names one of the applications that were asked about, has a
/// usable message id and cannot be older than the application; every text is shortened and stripped of control characters.
/// </summary>
public sealed class MailChecker
{
    /// <summary>The furthest back a scan looks, however old the first application is.</summary>
    private const int MaxLookbackDays = 120;

    private const int MaxRepliesPerPass = 200;

    private readonly IMailReader _reader;
    private readonly AccountService _accounts;
    private readonly ReplyStore _store;
    private readonly ILogger<MailChecker> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public MailChecker(IMailReader reader, AccountService accounts, ReplyStore store, ILogger<MailChecker> logger)
    {
        _reader = reader;
        _accounts = accounts;
        _store = store;
        _logger = logger;
    }

    public bool IsRunning => _running.CurrentCount == 0;

    /// <param name="rescan">Look back to the first application again, not only over the last few days: after a wrong match was fixed or a check was missed.</param>
    public async Task<MailCheckResult> CheckAsync(bool rescan, CancellationToken cancellationToken)
    {
        if (!await _running.WaitAsync(0, cancellationToken)) return new MailCheckResult(false, "A check is already running.", 0);

        try
        {
            return await RunAsync(rescan, cancellationToken);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<MailCheckResult> RunAsync(bool rescan, CancellationToken cancellationToken)
    {
        var applications = _accounts.Applications()
            .Where(a => a.Result is ApplyResultKind.Submitted or ApplyResultKind.AlreadyApplied)
            .ToList();
        if (applications.Count == 0)
        {
            _store.RecordCheck(DateTime.UtcNow, true, null, 0);
            return new MailCheckResult(true, "No applications have been submitted yet, so there are no replies to look for.", 0);
        }

        var now = DateTime.UtcNow;
        var earliest = Max(applications.Min(a => a.AppliedUtc).AddDays(-1), now.AddDays(-MaxLookbackDays));
        var lastSuccess = _store.State().LastSuccessUtc;
        // After a success only the last few days are looked at again (the overlap covers mail that arrived while it ran).
        var since = !rescan && lastSuccess is { } last && last.AddDays(-2) > earliest ? last.AddDays(-2) : earliest;

        IReadOnlyList<ScannedReply> scanned;
        try
        {
            scanned = await _reader.ScanAsync(new MailScanRequest(applications, since, _store.DismissedIds()), cancellationToken);
        }
        catch (MailReaderException ex)
        {
            var message = ex.IsUsageLimit ? "Claude's usage limit was reached, so the mailbox was not read. Try again later. (" + ex.Message + ")" : ex.Message;
            _store.RecordCheck(DateTime.UtcNow, false, message, 0);
            _logger.LogWarning("Mail check stopped: {Message}", message);
            return new MailCheckResult(false, message, 0);
        }

        var replies = Accept(scanned, applications, now);
        var added = _store.Upsert(replies);
        _store.RecordCheck(DateTime.UtcNow, true, null, added);

        _logger.LogInformation("Mail check: Claude reported {Reported} message(s), {Kept} kept, {New} new.", scanned.Count, replies.Count, added);
        return new MailCheckResult(true, Summary(replies.Count, added), added);
    }

    /// <summary>Checks what Claude reported against what was asked and turns it into replies.</summary>
    internal static List<EmployerReply> Accept(IEnumerable<ScannedReply> scanned, IReadOnlyList<ApplicationRecord> applications, DateTime nowUtc)
    {
        var byId = applications.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<EmployerReply>();

        foreach (var item in scanned)
        {
            if (accepted.Count >= MaxRepliesPerPass) break;
            if (item == null || !Guid.TryParse(item.ApplicationId?.Trim(), out var applicationId) || !byId.TryGetValue(applicationId, out var application)) continue;

            var messageId = Clean(item.MessageId, 200);
            if (messageId.Length == 0 || messageId.Any(char.IsWhiteSpace) || !seen.Add(messageId)) continue;

            var received = ParseDate(item.ReceivedUtc, nowUtc);
            if (received < application.AppliedUtc.AddDays(-1)) continue;   // it cannot be a reply to something not yet sent

            var kind = ParseKind(item.Kind);
            accepted.Add(new EmployerReply
            {
                MessageId = messageId,
                ApplicationId = application.Id,
                JobId = application.JobId,
                Company = application.Company,
                Role = application.Role,
                From = Clean(item.From, 120),
                Subject = Clean(item.Subject, 200),
                ReceivedUtc = received,
                Snippet = Clean(item.Snippet, 240),
                Kind = kind,
                Confident = item.Confident && kind != ReplyKind.Unclear,
                Reason = Clean(item.Reason, 90)
            });
        }
        return accepted;
    }

    /// <summary>"MovingForward", "moving forward" and "moving_forward" are all the same kind; anything else is not a kind this app knows.</summary>
    internal static ReplyKind ParseKind(string? text)
    {
        var key = new string((text ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return key switch
        {
            "acknowledged" or "received" => ReplyKind.Acknowledged,
            "inforequested" or "requestforinfo" => ReplyKind.InfoRequested,
            "movingforward" or "interview" => ReplyKind.MovingForward,
            "offer" => ReplyKind.Offer,
            "rejected" or "rejection" => ReplyKind.Rejected,
            _ => ReplyKind.Unclear
        };
    }

    private static DateTime ParseDate(string? text, DateTime nowUtc)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return nowUtc;
        var utc = parsed.UtcDateTime;
        return utc > nowUtc.AddDays(1) ? nowUtc : utc;
    }

    /// <summary>One line of text: no control characters or line breaks, shortened to <paramref name="max"/> characters.</summary>
    private static string Clean(string? text, int max)
    {
        var sb = new StringBuilder();
        foreach (var c in (text ?? "").Trim())
            sb.Append(char.IsControl(c) ? ' ' : c);

        var line = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        return line.Length <= max ? line : line[..max].TrimEnd() + "...";
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static string Summary(int found, int added) =>
        found == 0
            ? "No replies found in the mailbox."
            : $"Found {found} repl{(found == 1 ? "y" : "ies")}" + (added == 0 ? ", none of them new." : $", {added} new.");
}
