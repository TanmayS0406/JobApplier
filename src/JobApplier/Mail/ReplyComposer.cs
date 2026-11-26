using System.Collections.Concurrent;
using JobApplier.Services;

namespace JobApplier.Mail;

/// <summary>What opening, saving or sending an answer came to. <see cref="Message"/> is meant to be shown to the user as it is.</summary>
public sealed record ComposeResult(bool Ok, string Message)
{
    public EmployerReply? Reply { get; init; }
    public string Body { get; init; } = "";

    /// <summary>A remark from Claude for the user (what to fill in, what to check). Often empty.</summary>
    public string Note { get; init; } = "";

    /// <summary>The text is a draft that was saved earlier, not a new one: no Claude run was needed.</summary>
    public bool FromSaved { get; init; }
}

/// <summary>
/// Writing and sending an answer to an employer's mail. Claude writes a draft (it only reads the mail), the user reads and edits it,
/// and only the user's Send sends it - as exactly the text they approved. A draft is kept with the reply, so closing the window loses
/// nothing and no second Claude run is paid for.
/// </summary>
public sealed class ReplyComposer
{
    /// <summary>Pressing Send twice, or sending again from a second tab, must not mail the employer twice.</summary>
    private static readonly TimeSpan JustSent = TimeSpan.FromSeconds(30);

    private readonly IMailReader _reader;
    private readonly ReplyStore _store;
    private readonly ProfileService _profile;
    private readonly ILogger<ReplyComposer> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _busy = new();

    public ReplyComposer(IMailReader reader, ReplyStore store, ProfileService profile, ILogger<ReplyComposer> logger)
    {
        _reader = reader;
        _store = store;
        _profile = profile;
        _logger = logger;
    }

    /// <summary>
    /// The text to show for answering a reply: the saved draft if there is one, otherwise a new one from Claude (a minute or so).
    /// <paramref name="startOver"/> asks for a new one even when a draft is saved.
    /// </summary>
    public async Task<ComposeResult> DraftAsync(Guid replyId, bool startOver, CancellationToken cancellationToken)
    {
        var reply = _store.Find(replyId);
        if (reply == null) return new ComposeResult(false, "That reply is not in the list any more.");

        if (!startOver && reply.DraftBody.Length > 0)
            return new ComposeResult(true, "Your saved draft.") { Reply = reply, Body = reply.DraftBody, FromSaved = true };

        if (!_busy.TryAdd(replyId, 0)) return new ComposeResult(false, "An answer to this mail is already being written or sent.");
        try
        {
            var draft = await _reader.DraftReplyAsync(new DraftRequest(reply, _profile.Current), cancellationToken);
            _store.SaveDraft(replyId, draft.Body, DateTime.UtcNow);
            return new ComposeResult(true, "Claude wrote this from the mail. Read it, change what you like, then send.") { Reply = reply, Body = draft.Body, Note = draft.Note };
        }
        catch (MailReaderException ex)
        {
            _logger.LogWarning("Writing an answer stopped: {Message}", ex.Message);
            return new ComposeResult(false, Friendly(ex));
        }
        finally
        {
            _busy.TryRemove(replyId, out _);
        }
    }

    /// <summary>Keeps the text the user is working on.</summary>
    public ComposeResult SaveDraft(Guid replyId, string? body)
    {
        var text = ReplyText.Normalize(body);
        if (text.Length > ReplyText.MaxBody) return new ComposeResult(false, $"That is too long for an e-mail answer (at most {ReplyText.MaxBody} characters).");
        return _store.SaveDraft(replyId, text, DateTime.UtcNow)
            ? new ComposeResult(true, text.Length == 0 ? "The draft was removed." : "Draft saved.")
            : new ComposeResult(false, "That reply is not in the list any more.");
    }

    /// <summary>Sends the text as the answer to that mail. Nothing is sent unless it is a non-empty text with no unfilled <c>[[placeholder]]</c> left.</summary>
    public async Task<ComposeResult> SendAsync(Guid replyId, string? body, CancellationToken cancellationToken)
    {
        var reply = _store.Find(replyId);
        if (reply == null) return new ComposeResult(false, "That reply is not in the list any more.");

        var text = ReplyText.Normalize(body);
        if (text.Length == 0) return new ComposeResult(false, "The answer is empty. Write something first.");
        if (text.Length > ReplyText.MaxBody) return new ComposeResult(false, $"That is too long for an e-mail answer (at most {ReplyText.MaxBody} characters).");
        if (ReplyText.HasPlaceholder(text)) return new ComposeResult(false, "Part of the answer is still marked [[like this]]: write your own words there first. Nothing was sent.");
        if (reply.MessageId.Length == 0) return new ComposeResult(false, "This reply has no Gmail message to answer.");

        if (!_busy.TryAdd(replyId, 0)) return new ComposeResult(false, "An answer to this mail is already being written or sent.");
        try
        {
            // A second press that arrives after the first one finished (the page was slow to show it). Read again now that nobody else
            // is sending: the copy from before the lock may predate the first send.
            if (_store.Find(replyId)?.RepliedUtc is { } before && DateTime.UtcNow - before < JustSent)
                return new ComposeResult(false, "An answer to this mail was sent a moment ago. Nothing more was sent.");

            var outcome = await _reader.SendReplyAsync(new SendRequest(reply.MessageId, text), cancellationToken);
            _store.MarkReplied(replyId, DateTime.UtcNow);
            _logger.LogInformation("An answer to a reply from {Company} was sent.{Warning}", reply.Company, outcome.Warning == null ? "" : " Claude did not pass it on exactly as written.");

            var to = reply.FromAddress.Length > 0 ? " to " + reply.FromAddress : "";
            return new ComposeResult(true, $"Sent{to}." + (outcome.Warning == null ? "" : " " + outcome.Warning)) { Reply = _store.Find(replyId) };
        }
        catch (MailReaderException ex)
        {
            // What was written is not lost: it stays as the draft for the next try.
            _store.SaveDraft(replyId, text, DateTime.UtcNow);
            _logger.LogWarning("Sending an answer stopped: {Message}", ex.Message);
            return new ComposeResult(false, Friendly(ex));
        }
        finally
        {
            _busy.TryRemove(replyId, out _);
        }
    }

    private static string Friendly(MailReaderException ex) =>
        ex.IsUsageLimit ? "Claude's usage limit was reached. Try again later. (" + ex.Message + ")" : ex.Message;
}
