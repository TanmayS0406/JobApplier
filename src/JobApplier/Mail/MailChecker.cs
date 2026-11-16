using JobApplier.Domain;
using JobApplier.Services;

namespace JobApplier.Mail;

/// <summary>What one pass over the mailbox did. <paramref name="Message"/> is meant to be shown to the user as it is.</summary>
public sealed record MailCheckResult(bool Ok, string Message, int NewReplies, int Examined);

/// <summary>
/// Looks through the recent mail of the connected Gmail account for answers to the applications this app has made. Most mail is
/// dismissed from its header alone (sender, subject, date, snippet); only a message that could be an answer is read in full, and
/// even then only on this PC - nothing is sent anywhere but to Google. What is kept is the sender, subject, a short snippet and the verdict.
/// </summary>
public sealed class MailChecker
{
    /// <summary>How many message ids one pass asks Gmail for.</summary>
    private const int MaxListed = 400;

    /// <summary>How many new messages one pass reads. The rest are picked up by the next pass.</summary>
    private const int MaxPerPass = 250;

    private readonly IMailAccess _access;
    private readonly IGmailApi _gmail;
    private readonly AccountService _accounts;
    private readonly ProfileService _profile;
    private readonly ReplyStore _store;
    private readonly ILogger<MailChecker> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public MailChecker(IMailAccess access, IGmailApi gmail, AccountService accounts, ProfileService profile, ReplyStore store, ILogger<MailChecker> logger)
    {
        _access = access;
        _gmail = gmail;
        _accounts = accounts;
        _profile = profile;
        _store = store;
        _logger = logger;
    }

    public bool IsRunning => _running.CurrentCount == 0;

    /// <param name="rescan">Read every message again, not only the new ones: used after the rules changed or to repair a wrong match.</param>
    public async Task<MailCheckResult> CheckAsync(bool rescan, CancellationToken cancellationToken)
    {
        if (!_access.Connected) return new MailCheckResult(false, "Gmail is not connected.", 0, 0);
        if (!await _running.WaitAsync(0, cancellationToken)) return new MailCheckResult(false, "A check is already running.", 0, 0);

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
            return new MailCheckResult(true, "No applications have been submitted yet, so there are no replies to look for.", 0, 0);
        }

        _store.UseAccount(_access.Email);
        if (rescan) _store.ForgetSeen();

        // Mail from before the first application cannot be a reply. After a successful pass only the last few days are listed
        // again (the overlap covers messages that arrived while the previous pass was running).
        var earliest = applications.Min(a => a.AppliedUtc).AddDays(-1);
        var last = _store.State().LastSuccessUtc;
        var from = last is { } lastSuccess && lastSuccess.AddDays(-2) > earliest ? lastSuccess.AddDays(-2) : earliest;
        var query = $"after:{from:yyyy/MM/dd} -in:sent -in:drafts -in:chats -category:promotions -category:social";
        var self = string.IsNullOrWhiteSpace(_access.Email) ? _profile.Current.Email : _access.Email;

        var found = new List<EmployerReply>();
        var looked = new List<string>();
        var examined = 0;
        var failures = 0;
        string? error = null;
        var newReplies = 0;

        try
        {
            var ids = await _gmail.ListMessageIdsAsync(query, MaxListed, cancellationToken);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_store.HasSeen(id) || _store.IsDismissed(id)) continue;
                if (examined >= MaxPerPass) break;

                try
                {
                    var reply = await ExamineAsync(id, applications, self, cancellationToken);
                    examined++;
                    failures = 0;
                    looked.Add(id);
                    if (reply != null) found.Add(reply);
                }
                catch (GmailApiException ex) when (!ex.Unauthorized && ++failures < 3)
                {
                    // One message that will not load must not hold up the others; it is tried again next time.
                    _logger.LogWarning("Gmail message could not be read: {Message}", ex.Message);
                }
            }
        }
        catch (GmailApiException ex)
        {
            error = ex.Unauthorized ? "Google refused the saved sign-in. Connect Gmail again." : ex.Message;
            if (ex.Unauthorized) _access.MarkNeedsReconnect();
            _logger.LogWarning("Mail check stopped: {Message}", ex.Message);
        }
        finally
        {
            // Saved even when the pass is cancelled or fails half way, so the work is not repeated.
            newReplies = _store.Upsert(found);
            _store.MarkSeen(looked);
        }

        _store.RecordCheck(DateTime.UtcNow, error == null, error, newReplies);
        if (error != null) return new MailCheckResult(false, error, newReplies, examined);

        _logger.LogInformation("Mail check: {Examined} message(s) read, {New} new repl{Plural}.", examined, newReplies, newReplies == 1 ? "y" : "ies");
        return new MailCheckResult(true, Summary(examined, newReplies), newReplies, examined);
    }

    /// <summary>Reads one message and says which application it answers, or null when it answers none.</summary>
    private async Task<EmployerReply?> ExamineAsync(string id, IReadOnlyList<ApplicationRecord> applications, string self, CancellationToken cancellationToken)
    {
        var header = await _gmail.GetMetadataAsync(id, cancellationToken);
        if (header == null) return null;   // deleted since it was listed

        // The header settles most mail. The text is only fetched for a message that has a chance of being a reply.
        var byHeader = ReplyMatcher.Match(header, applications, self, useBody: false);
        if (byHeader == null && !ReplyMatcher.LooksLikeApplicationMail(header)) return null;

        var full = await _gmail.GetFullAsync(id, cancellationToken) ?? header;
        var match = ReplyMatcher.Match(full, applications, self, useBody: true);
        if (match == null) return null;

        var verdict = ReplyClassifier.Classify(full.Subject, full.Body.Length > 0 ? full.Body : full.Snippet);

        // A message that only mentions the company in its text and says nothing recognisable is a job alert or a newsletter, not a reply.
        if (verdict.Kind == ReplyKind.Unclear && !match.CompanyInHeader) return null;

        return new EmployerReply
        {
            MessageId = full.Id,
            ApplicationId = match.Application.Id,
            JobId = match.Application.JobId,
            Company = match.Application.Company,
            Role = match.Application.Role,
            From = Cut(full.From, 120),
            Subject = Cut(full.Subject, 200),
            ReceivedUtc = full.DateUtc,
            Snippet = Cut(full.Snippet, 240),
            Kind = verdict.Kind,
            Confident = verdict.Confident,
            Reason = verdict.Reason
        };
    }

    private static string Summary(int examined, int newReplies) =>
        examined == 0
            ? "Nothing new in the mailbox."
            : $"Read {examined} new message{(examined == 1 ? "" : "s")}. " +
              (newReplies == 0 ? "No new replies." : $"Found {newReplies} new repl{(newReplies == 1 ? "y" : "ies")}.");

    private static string Cut(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "...";
    }
}
