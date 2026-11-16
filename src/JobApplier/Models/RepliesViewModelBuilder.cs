using JobApplier.Domain;
using JobApplier.Mail;
using JobApplier.Services;

namespace JobApplier.Models;

public static class RepliesViewModelBuilder
{
    public static RepliesViewModel Build(ReplyStore replies, MailChecker checker, AccountService accounts)
    {
        var state = replies.State();
        return new RepliesViewModel
        {
            Enabled = state.Enabled,
            Checking = checker.IsRunning,
            ApplicationCount = accounts.Applications().Count(a => a.Result is ApplyResultKind.Submitted or ApplyResultKind.AlreadyApplied),
            LastCheckedUtc = state.LastCheckedUtc,
            LastError = state.LastError,
            Replies = replies.All()
        };
    }
}
