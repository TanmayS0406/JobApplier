using System.Collections.Concurrent;
using JobApplier.Domain;

namespace JobApplier.Services;

/// <summary>What the user did in the UI in response to a help request.</summary>
public sealed record HelpResponse(string Action, string Text);

public enum HelpWaitStatus
{
    /// <summary>The user responded; see <see cref="HelpWaitResult.Response"/>.</summary>
    Answered,

    /// <summary>Still waiting - the agent should call again with the same request id.</summary>
    Pending,

    /// <summary>The user never responded within the overall limit.</summary>
    TimedOut,

    /// <summary>The job was cancelled.</summary>
    Cancelled,

    /// <summary>The request id is unknown (already answered or the job ended).</summary>
    Unknown
}

public sealed record HelpWaitResult(HelpWaitStatus Status, HelpRequest? Request, HelpResponse? Response);

/// <summary>
/// Pauses a job until the user has dealt with something only a person can do: a captcha or bot check,
/// an e-mail verification, a question the profile cannot answer, or the final review before Submit.
/// <para>
/// The agent's tool call does not block for the whole wait. Each call waits one <c>slice</c> and then reports
/// <see cref="HelpWaitStatus.Pending"/>, so no HTTP request ever stays open long enough to hit a client timeout.
/// </para>
/// </summary>
public sealed class HumanHelpBroker
{
    private sealed class Pending
    {
        public required HelpRequest Request { get; init; }
        public required DateTime Deadline { get; init; }
        public TaskCompletionSource<HelpResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly JobStore _store;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    public HumanHelpBroker(JobStore store) => _store = store;

    /// <summary>Creates a request (when <paramref name="requestId"/> is null) or keeps waiting on an existing one.</summary>
    public async Task<HelpWaitResult> WaitAsync(
        Guid jobId, HelpKind kind, string message, string? requestId,
        TimeSpan slice, TimeSpan overallTimeout, CancellationToken cancellationToken)
    {
        Pending? pending;
        if (string.IsNullOrEmpty(requestId))
        {
            pending = _pending.GetOrAdd(jobId, _ => CreatePending(jobId, kind, message, overallTimeout));
        }
        else if (!_pending.TryGetValue(jobId, out pending) || pending.Request.Id != requestId)
        {
            return new HelpWaitResult(HelpWaitStatus.Unknown, null, null);
        }

        var finished = await Task.WhenAny(pending.Completion.Task, Task.Delay(slice, cancellationToken));

        // The caller's HTTP request was aborted: the agent's tool client gave up or disconnected. That happens in normal use
        // (a timeout, a restart, a killed process), so it is not an error and must not be thrown: the debugger stops on it and
        // ASP.NET only logs it. Nothing is consumed - not even an answer that arrived at the same moment, which would be lost
        // because nobody is listening - so the agent's next poll with the same request id carries on where this one stopped.
        if (cancellationToken.IsCancellationRequested)
            return new HelpWaitResult(HelpWaitStatus.Pending, pending.Request, null);

        if (finished == pending.Completion.Task)
        {
            var response = await pending.Completion.Task;
            Release(jobId, pending);
            return response.Action == "cancelled"
                ? new HelpWaitResult(HelpWaitStatus.Cancelled, pending.Request, response)
                : new HelpWaitResult(HelpWaitStatus.Answered, pending.Request, response);
        }

        if (DateTime.UtcNow >= pending.Deadline)
        {
            Release(jobId, pending);
            _store.Log(jobId, "warn", "Gave up waiting for you.");
            return new HelpWaitResult(HelpWaitStatus.TimedOut, pending.Request, null);
        }
        return new HelpWaitResult(HelpWaitStatus.Pending, pending.Request, null);
    }

    /// <summary>Called from the UI. Returns the request that was answered, or null if nothing matching is pending.</summary>
    public HelpRequest? Resolve(Guid jobId, string requestId, HelpResponse response)
    {
        if (!_pending.TryGetValue(jobId, out var pending) || pending.Request.Id != requestId) return null;
        if (!pending.Completion.TrySetResult(response)) return null;

        _store.Update(jobId, r => r.PendingHelp = null);
        _store.Log(jobId, "help", "You responded: " + Describe(response));
        return pending.Request;
    }

    /// <summary>Releases anything waiting on this job (used when the job is cancelled or ends).</summary>
    public void CancelJob(Guid jobId)
    {
        if (_pending.TryGetValue(jobId, out var pending))
        {
            pending.Completion.TrySetResult(new HelpResponse("cancelled", ""));
            _store.Update(jobId, r => r.PendingHelp = null);
        }
    }

    public bool IsWaiting(Guid jobId) => _pending.ContainsKey(jobId);

    private Pending CreatePending(Guid jobId, HelpKind kind, string message, TimeSpan overallTimeout)
    {
        var request = new HelpRequest { Kind = kind, Message = message.Trim() };
        _store.Update(jobId, r => r.PendingHelp = request);
        _store.Log(jobId, "help", $"Needs you ({kind}): {request.Message}");
        return new Pending { Request = request, Deadline = DateTime.UtcNow + overallTimeout };
    }

    private void Release(Guid jobId, Pending pending)
    {
        _pending.TryRemove(new KeyValuePair<Guid, Pending>(jobId, pending));
        _store.Update(jobId, r =>
        {
            if (r.PendingHelp?.Id == pending.Request.Id) r.PendingHelp = null;
        });
    }

    private static string Describe(HelpResponse response) => response.Action switch
    {
        "answer" => "answered the question",
        "yes" => "said yes",
        "approve" => "approved the submission",
        "submitted" => "submitted it yourself",
        "skip" => "skipped this job",
        "done" => "done",
        _ => response.Action
    };
}
