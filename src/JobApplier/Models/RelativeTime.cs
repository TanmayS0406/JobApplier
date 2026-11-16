namespace JobApplier.Models;

public static class RelativeTime
{
    /// <summary>"just now", "12 min ago", "5 h ago", "3 d ago".</summary>
    public static string Ago(DateTime utc, DateTime? nowUtc = null)
    {
        var span = (nowUtc ?? DateTime.UtcNow) - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }
}
