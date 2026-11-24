using System.Globalization;
using System.Text.RegularExpressions;

namespace JobApplier.Infrastructure;

/// <summary>
/// Recognises "you have run out of tokens / quota" errors from the model CLIs and works out when they should be over.
/// Waiting fixes this kind of failure (unlike a bad prompt or a broken site), so it is handled differently from other errors:
/// the job can be retried automatically once the limit has reset.
/// </summary>
public static class UsageLimit
{
    /// <summary>Never trust a reset time further away than this (the longest real limit is a week).</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromDays(8);

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Wording used by Claude Code ("5-hour limit reached", "usage limit reached", "weekly limit") and GitHub Copilot ("rate limited",
    // "quota exceeded", "premium requests ... exhausted"). The Copilot CLI also names the kind of error (rate_limit, quota), which the
    // agent reads directly; these patterns are for the cases where only text is left.
    private static readonly Regex[] Signals =
    {
        new(@"usage limit", Ci),
        new(@"\b(5[- ]?hour|five[- ]?hour|weekly|daily|monthly|session)\s+(usage\s+)?limit", Ci),
        new(@"\b(hit|reached|exceeded)\s+(your|the)\s+([\w-]+\s+)?(usage\s+)?limit", Ci),
        new(@"limit\s+reached", Ci),
        new(@"rate[\s_-]?limit", Ci),
        new(@"too many requests", Ci),
        new(@"resource[\s_]exhausted", Ci),
        new(@"exceeded your (current )?quota|quota (exceeded|exhausted|will reset)|exhausted your (capacity|quota)", Ci),
        new(@"(premium requests?|ai credits?)[^.\n]{0,80}(exhaust|exceed|used up|run out|ran out|no more|limit)", Ci),
        new(@"(run out of|out of|used (all|up)|exhausted)[^.\n]{0,40}(premium requests?|ai credits?)", Ci),
    };

    private static readonly Regex Http429 = new(@"\b429\b", Ci);
    private static readonly Regex Http429Context = new(@"limit|quota|exhaust|too many|rate", Ci);

    private static readonly Regex Epoch = new(@"(?:\||resets?_at""?\s*[:=]\s*)(\d{10})(?!\d)", Ci);

    private static readonly Regex Relative = new(
        @"(?:reset|retry|try again)[^0-9]{0,24}?(?:after|in)\s+(?<d>(?:\d+(?:\.\d+)?\s*(?:h(?:ours?|rs?)?|m(?:in(?:ute)?s?)?|s(?:ec(?:ond)?s?)?)(?![a-z])\s*(?:and\s*|,\s*)?)+)", Ci);

    private static readonly Regex DurationPart = new(@"(\d+(?:\.\d+)?)\s*(h|m|s)", Ci);

    private static readonly Regex Clock = new(
        @"resets?\s+(?:on\s+)?(?:(?<dow>mon|tue|wed|thu|fri|sat|sun)[a-z]*,?\s+)?" +
        @"(?:(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+)?" +
        @"(?:at\s+)?(?<h>\d{1,2})(?::(?<min>\d{2}))?\s*(?<ap>am|pm)?(?![\d/-])(?:\s*\((?<tz>[^)]+)\))?", Ci);

    /// <summary>True when the text says a token, quota or rate limit stopped the request.</summary>
    public static bool IsLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (Signals.Any(s => s.IsMatch(text))) return true;
        return Http429.IsMatch(text) && Http429Context.IsMatch(text);
    }

    /// <summary>When the limit is expected to be over, read from the error text. Null when the text does not say.</summary>
    public static DateTime? ResetTime(string? text, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        DateTime? found = null;

        var epoch = Epoch.Match(text);
        if (epoch.Success && long.TryParse(epoch.Groups[1].Value, CultureInfo.InvariantCulture, out var seconds))
            found = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

        found ??= FromRelative(text, nowUtc) ?? FromClock(text, nowUtc);

        if (found is not { } at || at <= nowUtc || at - nowUtc > LongestWait) return null;
        return DateTime.SpecifyKind(at, DateTimeKind.Utc);
    }

    /// <summary>How long to wait before probing again when the error did not say when the limit resets: 3, 10, 20, 30, then 60 minutes.</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromMinutes(attempt switch { <= 0 => 3, 1 => 10, 2 => 20, 3 => 30, _ => 60 });

    /// <summary>"about Tue 15:00 (in 2 h 10 min)" for a status line.</summary>
    public static string Describe(DateTime utc, DateTime nowUtc)
    {
        var left = utc - nowUtc;
        var wait = left <= TimeSpan.Zero ? "now" : left.TotalMinutes < 90 ? $"in {Math.Max(1, (int)Math.Round(left.TotalMinutes))} min" : $"in {(int)left.TotalHours} h {left.Minutes} min";
        return $"about {utc.ToLocalTime():ddd HH:mm} ({wait})";
    }

    private static DateTime? FromRelative(string text, DateTime nowUtc)
    {
        var match = Relative.Match(text);
        if (!match.Success) return null;

        double seconds = 0;
        foreach (Match part in DurationPart.Matches(match.Groups["d"].Value))
        {
            var value = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            seconds += char.ToLowerInvariant(part.Groups[2].Value[0]) switch { 'h' => value * 3600, 'm' => value * 60, _ => value };
        }
        return seconds <= 0 ? null : nowUtc.AddSeconds(Math.Ceiling(seconds) + 1);
    }

    private static DateTime? FromClock(string text, DateTime nowUtc)
    {
        var match = Clock.Match(text);
        if (!match.Success) return null;

        var zone = TimeZoneInfo.Local;
        if (match.Groups["tz"].Success)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["tz"].Value.Trim()); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { /* keep the local zone */ }
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["min"].Success ? int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture) : 0;
        if (match.Groups["ap"].Success)
        {
            if (hour is < 1 or > 12) return null;
            hour = hour % 12 + (match.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        }
        if (hour > 23 || minute > 59) return null;

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        DateTime candidate;

        if (match.Groups["mon"].Success)
        {
            var month = DateTime.ParseExact(match.Groups["mon"].Value[..3], "MMM", CultureInfo.InvariantCulture).Month;
            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            if (day < 1 || day > DateTime.DaysInMonth(nowLocal.Year, month)) return null;
            candidate = new DateTime(nowLocal.Year, month, day, hour, minute, 0);
            if (candidate <= nowLocal) candidate = candidate.AddYears(1);
        }
        else
        {
            candidate = nowLocal.Date.AddHours(hour).AddMinutes(minute);
            if (match.Groups["dow"].Success)
            {
                var target = (DayOfWeek)Array.IndexOf(new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }, match.Groups["dow"].Value[..3].ToLowerInvariant());
                while (candidate.DayOfWeek != target || candidate <= nowLocal) candidate = candidate.AddDays(1);
            }
            else if (candidate <= nowLocal)
            {
                candidate = candidate.AddDays(1);
            }
        }

        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), zone);
        }
        catch (ArgumentException)
        {
            return null; // a time that does not exist (daylight-saving gap)
        }
    }
}
