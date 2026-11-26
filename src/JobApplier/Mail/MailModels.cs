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

    /// <summary>The answer the user was writing to this mail (Claude's draft, or their own edit of it). Kept so closing the window loses nothing.</summary>
    public string DraftBody { get; set; } = "";

    public DateTime? DraftSavedUtc { get; set; }

    /// <summary>When an answer was sent from here. Null: none was.</summary>
    public DateTime? RepliedUtc { get; set; }

    [JsonIgnore]
    public string GmailLink => "https://mail.google.com/mail/u/0/#all/" + Uri.EscapeDataString(MessageId);

    /// <summary>The subject an answer to this mail gets (Gmail adds the "Re:" itself; this is what the user is shown).</summary>
    [JsonIgnore]
    public string AnswerSubject => Subject.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? Subject : "Re: " + Subject;

    /// <summary>The address in <see cref="From"/> (<c>Acme Jobs &lt;jobs@acme.example&gt;</c> or a bare address); empty when there is none.</summary>
    [JsonIgnore]
    public string FromAddress
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(From, @"<\s*([^<>\s]+@[^<>\s]+)\s*>");
            if (match.Success) return match.Groups[1].Value;
            var bare = From.Trim();
            return bare.Contains('@') && !bare.Any(char.IsWhiteSpace) ? bare : "";
        }
    }

    /// <summary>Recruiting systems write from addresses nobody reads. An answer to one of them goes nowhere, and the user should know before writing it.</summary>
    [JsonIgnore]
    public bool IsNoReply
    {
        get
        {
            var address = FromAddress;
            var local = address.Contains('@') ? address[..address.IndexOf('@')] : "";
            return local.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(local,
                @"^(no[-_.]?reply|do[-_.]?not[-_.]?reply|donotreply|mailer[-_.]?daemon|postmaster|bounces?|notifications?)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
    }

    /// <summary>A copy that can be read without holding the store's lock.</summary>
    public EmployerReply Copy() => (EmployerReply)MemberwiseClone();
}
