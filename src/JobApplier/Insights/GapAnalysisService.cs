using JobApplier.Domain;
using JobApplier.Services;

namespace JobApplier.Insights;

/// <summary>
/// Feeds the resume gap analysis from the app's own data - every job that has a match score, and the resume text - and keeps the last
/// report, so opening the page again (or the page refreshing a part of itself) does not run the analysis again until something changed.
/// </summary>
public sealed class GapAnalysisService
{
    /// <summary>A posting shorter than this is a screenshot snippet, not a description worth reading as text.</summary>
    internal const int MinTextLength = 200;

    private readonly JobStore _jobs;
    private readonly ProfileService _profile;
    private readonly object _gate = new();
    private (string Fingerprint, GapReport Report)? _last;

    public GapAnalysisService(JobStore jobs, ProfileService profile)
    {
        _jobs = jobs;
        _profile = profile;
    }

    public string ResumeText => _profile.Current.ResumeText ?? "";

    /// <summary>Every job that has been scored, copied out of the store.</summary>
    public IReadOnlyList<AnalyzedJob> Inputs() => _jobs.ReadAll(ToAnalyzed).OfType<AnalyzedJob>().ToList();

    public GapReport Analyze(int threshold)
    {
        var resume = ResumeText;
        var jobs = Inputs();
        var options = new AnalysisOptions(threshold);
        var fingerprint = GapAnalyzer.Fingerprint(resume, jobs, options);

        // One analysis at a time: it takes a second or two, and two page loads at once should share the answer.
        lock (_gate)
        {
            if (_last is { } last && last.Fingerprint == fingerprint) return last.Report;

            var report = GapAnalyzer.Analyze(resume, jobs, options);
            _last = (fingerprint, report);
            return report;
        }
    }

    /// <summary>Copies what the analysis needs out of a job (called under the job's lock, so it must not hold on to the record).</summary>
    internal static AnalyzedJob? ToAnalyzed(JobRecord job)
    {
        var match = job.Match;
        if (match == null) return null;

        // The full posting text read from the company's page is best; the screenshot's own description is the fallback.
        var text = job.Found is { Found: true } found && found.Description.Length >= MinTextLength ? found.Description : job.Extracted?.Description ?? "";

        return new AnalyzedJob(
            job.Id, job.DisplayCompany, job.DisplayTitle, match.Percent,
            match.SkillsScore, match.ExperienceScore, match.DomainScore, match.SeniorityScore, match.LogisticsScore,
            match.MatchedSkills.ToList(), match.MissingSkills.ToList(), text);
    }
}
