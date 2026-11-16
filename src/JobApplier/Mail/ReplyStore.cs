using JobApplier.Infrastructure;

namespace JobApplier.Mail;

/// <summary>When Gmail was last checked, and which messages have been looked at so they are not read again.</summary>
public sealed class MailState
{
    /// <summary>The mailbox this state belongs to. A different account starts again.</summary>
    public string Account { get; set; } = "";

    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string LastError { get; set; } = "";
    public int LastNewReplies { get; set; }

    /// <summary>Messages already examined (matched or not), oldest first.</summary>
    public List<string> SeenIds { get; set; } = new();

    /// <summary>Messages the user said are not about the application they were matched to. They are never matched again.</summary>
    public List<string> DismissedIds { get; set; } = new();
}

/// <summary>
/// The replies found so far (<c>replies.json</c>) and the checking state (<c>mail-state.json</c>). A reply keeps only the sender,
/// subject, a short snippet and the verdict - never the message itself.
/// </summary>
public sealed class ReplyStore
{
    private const int MaxSeen = 6000;
    private const int MaxDismissed = 1000;

    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly List<EmployerReply> _replies;
    private readonly MailState _state;
    private readonly HashSet<string> _seen;
    private readonly HashSet<string> _dismissed;

    public ReplyStore(AppPaths paths)
    {
        _paths = paths;
        _replies = JsonFile.Read<List<EmployerReply>>(paths.RepliesFile) ?? new List<EmployerReply>();
        _state = JsonFile.Read<MailState>(paths.MailStateFile) ?? new MailState();
        _seen = new HashSet<string>(_state.SeenIds, StringComparer.Ordinal);
        _dismissed = new HashSet<string>(_state.DismissedIds, StringComparer.Ordinal);
    }

    /// <summary>Every reply, newest first.</summary>
    public IReadOnlyList<EmployerReply> All()
    {
        lock (_gate) return _replies.OrderByDescending(r => r.ReceivedUtc).ToList();
    }

    public int Count
    {
        get { lock (_gate) return _replies.Count; }
    }

    /// <summary>
    /// What each application's mail says now, by application id. A decisive message (offer, rejection, invitation, request) outranks a
    /// later automatic "received" note; among decisive ones the newest wins.
    /// </summary>
    public IReadOnlyDictionary<Guid, EmployerReply> StatusByApplication()
    {
        lock (_gate) return _replies.GroupBy(r => r.ApplicationId).ToDictionary(g => g.Key, Latest);
    }

    /// <summary>The same, by job id: the results table lists jobs.</summary>
    public IReadOnlyDictionary<Guid, EmployerReply> StatusByJob()
    {
        lock (_gate) return _replies.GroupBy(r => r.JobId).ToDictionary(g => g.Key, Latest);
    }

    private static EmployerReply Latest(IEnumerable<EmployerReply> group) =>
        group.Where(r => IsDecisive(r.Kind)).OrderByDescending(r => r.ReceivedUtc).FirstOrDefault()
        ?? group.OrderByDescending(r => r.ReceivedUtc).First();

    public static bool IsDecisive(ReplyKind kind) => kind is ReplyKind.Offer or ReplyKind.Rejected or ReplyKind.MovingForward or ReplyKind.InfoRequested;

    public MailState State()
    {
        lock (_gate)
        {
            return new MailState
            {
                Account = _state.Account,
                LastCheckedUtc = _state.LastCheckedUtc,
                LastSuccessUtc = _state.LastSuccessUtc,
                LastError = _state.LastError,
                LastNewReplies = _state.LastNewReplies
            };
        }
    }

    public bool HasSeen(string messageId)
    {
        lock (_gate) return _seen.Contains(messageId);
    }

    public bool IsDismissed(string messageId)
    {
        lock (_gate) return _dismissed.Contains(messageId);
    }

    /// <summary>Starts again for a different mailbox: what was seen in the old one says nothing about the new one.</summary>
    public void UseAccount(string email)
    {
        lock (_gate)
        {
            if (string.Equals(_state.Account, email, StringComparison.OrdinalIgnoreCase)) return;
            _state.Account = email;
            _seen.Clear();
            _state.SeenIds.Clear();
            _state.LastSuccessUtc = null;
            SaveStateLocked();
        }
    }

    /// <summary>Adds replies, or refreshes the ones already stored for the same message. Returns how many were new.</summary>
    public int Upsert(IEnumerable<EmployerReply> replies)
    {
        lock (_gate)
        {
            var added = 0;
            foreach (var reply in replies)
            {
                if (_dismissed.Contains(reply.MessageId)) continue;

                var existing = _replies.FirstOrDefault(r => r.MessageId == reply.MessageId);
                if (existing == null)
                {
                    _replies.Add(reply);
                    added++;
                    continue;
                }

                // A re-scan re-reads the message: keep the row, refresh what the rules decide.
                existing.ApplicationId = reply.ApplicationId;
                existing.JobId = reply.JobId;
                existing.Company = reply.Company;
                existing.Role = reply.Role;
                existing.Kind = reply.Kind;
                existing.Confident = reply.Confident;
                existing.Reason = reply.Reason;
            }
            JsonFile.Write(_paths.RepliesFile, _replies);
            return added;
        }
    }

    /// <summary>Notes that messages have been examined, whether or not they were replies.</summary>
    public void MarkSeen(IEnumerable<string> messageIds)
    {
        lock (_gate)
        {
            foreach (var id in messageIds)
                if (_seen.Add(id)) _state.SeenIds.Add(id);
            SaveStateLocked();
        }
    }

    /// <summary>Forgets what was seen, so the next check reads everything again (a re-scan).</summary>
    public void ForgetSeen()
    {
        lock (_gate)
        {
            _seen.Clear();
            _state.SeenIds.Clear();
            _state.LastSuccessUtc = null;
            SaveStateLocked();
        }
    }

    public void RecordCheck(DateTime utc, bool success, string? error, int newReplies)
    {
        lock (_gate)
        {
            _state.LastCheckedUtc = utc;
            if (success) _state.LastSuccessUtc = utc;
            _state.LastError = success ? "" : (error ?? "");
            _state.LastNewReplies = success ? newReplies : _state.LastNewReplies;
            SaveStateLocked();
        }
    }

    /// <summary>The user says this message is not about that application: it is removed and never matched again.</summary>
    public bool Dismiss(Guid replyId)
    {
        lock (_gate)
        {
            var reply = _replies.FirstOrDefault(r => r.Id == replyId);
            if (reply == null) return false;

            _replies.Remove(reply);
            if (_dismissed.Add(reply.MessageId)) _state.DismissedIds.Add(reply.MessageId);
            while (_state.DismissedIds.Count > MaxDismissed) { _dismissed.Remove(_state.DismissedIds[0]); _state.DismissedIds.RemoveAt(0); }

            JsonFile.Write(_paths.RepliesFile, _replies);
            SaveStateLocked();
            return true;
        }
    }

    /// <summary>Deletes every saved reply and everything remembered about the mailbox.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _replies.Clear();
            _seen.Clear();
            _dismissed.Clear();
            _state.SeenIds.Clear();
            _state.DismissedIds.Clear();
            _state.LastCheckedUtc = null;
            _state.LastSuccessUtc = null;
            _state.LastError = "";
            _state.LastNewReplies = 0;
            JsonFile.Write(_paths.RepliesFile, _replies);
            SaveStateLocked();
        }
    }

    private void SaveStateLocked()
    {
        if (_state.SeenIds.Count > MaxSeen)
        {
            var drop = _state.SeenIds.Count - MaxSeen;
            foreach (var id in _state.SeenIds.Take(drop)) _seen.Remove(id);
            _state.SeenIds.RemoveRange(0, drop);
        }
        JsonFile.Write(_paths.MailStateFile, _state);
    }
}
