using System.Text.Json.Serialization;

namespace JobApplier.Mail;

/// <summary>What an employer's reply says about the application, in the order a job seeker cares about it.</summary>
public enum ReplyKind
{
    /// <summary>"We received your application" - an automatic acknowledgement, nothing decided yet.</summary>
    Acknowledged,

    /// <summary>Not clear what it says (it is about the application, but no rule recognised it).</summary>
    Unclear,

    /// <summary>They want something from the candidate: more information, a form, a document.</summary>
    InfoRequested,

    /// <summary>Good news: an interview, a call, an assessment, "next steps".</summary>
    MovingForward,

    /// <summary>An offer of the job.</summary>
    Offer,

    /// <summary>"We will not be moving forward".</summary>
    Rejected
}

public static class ReplyKindExtensions
{
    public static string Label(this ReplyKind kind) => kind switch
    {
        ReplyKind.Offer => "Offer",
        ReplyKind.MovingForward => "Moving forward",
        ReplyKind.InfoRequested => "Info requested",
        ReplyKind.Rejected => "Rejected",
        ReplyKind.Acknowledged => "Received",
        _ => "Reply"
    };

    /// <summary>The CSS class suffix of the chip: <c>reply-offer</c>, <c>reply-rejected</c>, ...</summary>
    public static string Css(this ReplyKind kind) => kind switch
    {
        ReplyKind.Offer => "reply-offer",
        ReplyKind.MovingForward => "reply-moving",
        ReplyKind.InfoRequested => "reply-info",
        ReplyKind.Rejected => "reply-rejected",
        ReplyKind.Acknowledged => "reply-ack",
        _ => "reply-unclear"
    };
}

/// <summary>One e-mail as far as this app needs it. <paramref name="Body"/> is empty until the full message has been fetched.</summary>
public sealed record MailMessage(
    string Id,
    string ThreadId,
    string FromName,
    string FromAddress,
    string Subject,
    DateTime DateUtc,
    string Snippet,
    IReadOnlyList<string> Labels,
    string Body = "")
{
    /// <summary>"Jane at Acme &lt;jobs@acme.com&gt;" as it would be shown.</summary>
    public string From => FromName.Length > 0 ? $"{FromName} <{FromAddress}>" : FromAddress;

    public string Domain => FromAddress.Contains('@') ? FromAddress[(FromAddress.LastIndexOf('@') + 1)..].ToLowerInvariant() : "";
}

/// <summary>
/// A reply to an application, as saved. Only what is needed to show it is kept - sender, subject, date and a short snippet - never
/// the whole message, which stays in Gmail (<see cref="GmailLink"/> opens it).
/// </summary>
public sealed class EmployerReply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MessageId { get; set; } = "";

    /// <summary>The <c>ApplicationRecord</c> this reply was matched to.</summary>
    public Guid ApplicationId { get; set; }

    /// <summary>The job that made that application (it may have been deleted since; the reply stays).</summary>
    public Guid JobId { get; set; }

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime ReceivedUtc { get; set; }
    public string Snippet { get; set; } = "";
    public ReplyKind Kind { get; set; }

    /// <summary>False when the wording pointed two ways (for example a rejection that also mentions an interview).</summary>
    public bool Confident { get; set; } = true;

    /// <summary>The phrase that decided the classification, so the result can be checked at a glance.</summary>
    public string Reason { get; set; } = "";

    [JsonIgnore]
    public string GmailLink => "https://mail.google.com/mail/u/0/#all/" + Uri.EscapeDataString(MessageId);
}
