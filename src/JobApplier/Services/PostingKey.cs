using JobApplier.Domain;

namespace JobApplier.Services;

/// <summary>
/// Identifies "the same posting" across jobs so the app never submits two applications to one position, for example when the
/// same screenshot is uploaded twice or two screenshots lead to the same careers-site page.
/// </summary>
public static class PostingKey
{
    // Parameters that identify where a visitor came from, not which job it is.
    private static readonly string[] TrackingParameters = { "utm_", "gh_src", "source", "ref", "referrer", "src", "trk", "fbclid", "gclid", "lever-source", "lever-origin" };

    /// <summary>A comparable key for a posting address, or null when it is not a usable web address.</summary>
    public static string? From(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();

        // Some systems identify the job only in the query string (?gh_jid=123, ?jobId=9), so keep what is not tracking.
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsTracking(p.Split('=')[0]))
            .Select(p => p.ToLowerInvariant())
            .OrderBy(p => p, StringComparer.Ordinal);

        return host + path + "?" + string.Join('&', query);
    }

    /// <summary>
    /// Another job that has already applied to, or is currently applying to, the same posting. <paramref name="candidates"/> holds
    /// every job's id, posting address and status.
    /// </summary>
    public static Guid? FindDuplicate(Guid self, string? url, IEnumerable<(Guid Id, string? Url, JobStatus Status)> candidates)
    {
        var key = From(url);
        if (key == null) return null;

        foreach (var other in candidates)
        {
            if (other.Id == self) continue;
            if (other.Status is not (JobStatus.Applied or JobStatus.Applying)) continue;
            if (From(other.Url) == key) return other.Id;
        }
        return null;
    }

    private static bool IsTracking(string name) =>
        TrackingParameters.Any(t => t.EndsWith('_')
            ? name.StartsWith(t, StringComparison.OrdinalIgnoreCase)
            : name.Equals(t, StringComparison.OrdinalIgnoreCase));
}
