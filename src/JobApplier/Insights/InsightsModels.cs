namespace JobApplier.Insights;

/// <summary>One scored job as the analysis sees it: only what the analysis needs, copied out of the job store.</summary>
/// <param name="Percent">The match percentage Claude gave this job for the current resume.</param>
/// <param name="Matched">Skills the job asks for that the resume shows, in the job's own words.</param>
/// <param name="Missing">Skills the job asks for that the resume does not show.</param>
/// <param name="Text">The posting text (empty when only the screenshot was read and it had no description).</param>
public sealed record AnalyzedJob(
    Guid Id, string Company, string Title, int Percent,
    int SkillsScore, int ExperienceScore, int DomainScore, int SeniorityScore, int LogisticsScore,
    IReadOnlyList<string> Matched, IReadOnlyList<string> Missing, string Text);

/// <summary>A skill the resume lacks, merged from the different ways the postings word it.</summary>
/// <param name="Variants">The wordings found in the jobs ("Kubernetes", "K8s", ...), most common first.</param>
/// <param name="Jobs">In how many jobs it is missing.</param>
/// <param name="Share">Jobs / all scored jobs.</param>
/// <param name="AveragePercent">The average match of the jobs that miss it.</param>
/// <param name="InResumeText">The resume's text already contains the words, although the job was marked as missing it: the skill may just be buried or phrased differently.</param>
/// <param name="Soft">A general trait (communication, teamwork) rather than something a resume change can add.</param>
/// <param name="Eligibility">A requirement about the person (work authorisation, visa, relocation, location): a resume can state it if true but cannot gain it.</param>
public sealed record SkillGap(string Skill, IReadOnlyList<string> Variants, int Jobs, double Share, double AveragePercent, bool InResumeText, bool Soft, bool Eligibility = false)
{
    /// <summary>Whether adding it to a resume is the kind of change this analysis recommends.</summary>
    public bool Addable => !Soft && !Eligibility;
}

/// <summary>What one added skill would do to one job.</summary>
public sealed record JobShift(Guid JobId, string Company, string Title, int From, int To);

/// <summary>
/// One step of the plan: "add this next". The steps are chosen one after the other, each time the skill that makes the most
/// additional jobs relevant (a job is relevant when its match reaches the threshold).
/// </summary>
/// <param name="NewlyRelevant">Jobs that cross the threshold because of this step.</param>
/// <param name="TotalRelevant">Relevant jobs after this step and all the earlier ones.</param>
/// <param name="AveragePoints">Average match points gained by the jobs this skill is missing from.</param>
/// <param name="JobsMissingIt">How many jobs miss this skill.</param>
public sealed record Recommendation(
    int Rank, string Skill, IReadOnlyList<string> Variants, int NewlyRelevant, int TotalRelevant, double AveragePoints, int JobsMissingIt,
    bool InResumeText, IReadOnlyList<JobShift> Shifts);

/// <summary>How many jobs are relevant before the plan, after it, and how sure that is.</summary>
/// <param name="Expected">After the plan, using the calibrated estimate.</param>
/// <param name="Low">The 10th percentile of a simulation of the estimate's uncertainty.</param>
/// <param name="High">The 90th percentile.</param>
public sealed record Projection(int Baseline, int Expected, int Low, int High, int Total);

/// <summary>A group of similar jobs found by K-Means on their posting text.</summary>
public sealed record JobFamily(int Id, string Label, IReadOnlyList<string> TopTerms, int Jobs, double AveragePercent, int Relevant, IReadOnlyList<SkillGap> TopGaps);

/// <summary>A word or phrase that many postings use and the resume never does (what an applicant tracking system would look for).</summary>
public sealed record KeywordGap(string Term, int Jobs, double Share);

/// <summary>
/// How "one more skill" was turned into match points. <see cref="Method"/> is <c>calibrated</c> when a regression learned from the user's
/// own scored jobs was good enough to trust, otherwise <c>assumed</c> (every missing skill closes an equal share of the skills gap).
/// </summary>
public sealed record Calibration(string Method, int Samples, double Slope, double Intercept, double RSquared, double Correlation, string Note);

public sealed record GapReport(
    int Threshold,
    int ScoredJobs,
    int JobsWithText,
    int RelevantNow,
    double AveragePercent,
    IReadOnlyList<SkillGap> Gaps,
    IReadOnlyList<Recommendation> Recommendations,
    Projection Projection,
    IReadOnlyList<JobFamily> Families,
    IReadOnlyList<KeywordGap> Keywords,
    Calibration Calibration,
    double? TextAgreement,
    IReadOnlyList<string> Notes,
    string Fingerprint)
{
    public bool HasData => ScoredJobs > 0;
}

public sealed record AnalysisOptions(int Threshold = 70, int MaxRecommendations = 8, int Seed = 7);
