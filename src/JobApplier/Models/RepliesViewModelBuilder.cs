using JobApplier.Domain;
using JobApplier.Mail;
using JobApplier.Services;

namespace JobApplier.Models;

public static class RepliesViewModelBuilder
{
    public static RepliesViewModel Build(GmailConnection gmail, ReplyStore replies, MailChecker checker, AccountService accounts)
    {
        var status = gmail.GetStatus();
        var state = replies.State();
        return new RepliesViewModel
        {
            HasClient = status.HasClient,
            Connected = status.Connected,
            NeedsReconnect = status.NeedsReconnect,
            Email = status.Email,
            Checking = checker.IsRunning,
            ApplicationCount = accounts.Applications().Count(a => a.Result is ApplyResultKind.Submitted or ApplyResultKind.AlreadyApplied),
            LastCheckedUtc = state.LastCheckedUtc,
            LastError = state.LastError,
            Replies = replies.All()
        };
    }
}
