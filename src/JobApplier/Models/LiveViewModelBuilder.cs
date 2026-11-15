using JobApplier.Services;

namespace JobApplier.Models;

public static class LiveViewModelBuilder
{
    public static LiveViewModel Build(JobStore store, DateTime? pausedUntilUtc = null)
    {
        var rows = store.ReadAll(JobRow.From);
        var help = store
            .ReadAll(r => r.PendingHelp == null ? null : new HelpItem(r.Id, r.DisplayCompany, r.DisplayTitle, r.PendingHelp))
            .OfType<HelpItem>()
            .ToList();
        return new LiveViewModel { Rows = rows, Help = help, PausedUntilUtc = pausedUntilUtc };
    }
}
