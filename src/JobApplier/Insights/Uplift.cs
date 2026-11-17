using JobApplier.Domain;
using MathNet.Numerics;
using MathNet.Numerics.Statistics;

namespace JobApplier.Insights;

/// <summary>A job with its skills reduced to canonical keys: what the optimizer needs to ask "what if the resume also had X".</summary>
internal sealed class JobFacts
{
    public JobFacts(AnalyzedJob job, HashSet<string> missingKeys, int matchedCount)
    {
        Job = job;
        MissingKeys = missingKeys;
        MatchedCount = matchedCount;
    }

    public AnalyzedJob Job { get; }

    /// <summary>The skills the job wants that the resume does not show, as canonical keys.</summary>
    public HashSet<string> MissingKeys { get; }

    /// <summary>How many of the job's skills the resume does show.</summary>
    public int MatchedCount { get; }

    /// <summary>All the skills the job asked for.</summary>
    public int Total => MatchedCount + MissingKeys.Count;

    /// <summary>The share of the job's skills the resume covers (0..1).</summary>
    public double Coverage => Total == 0 ? 1 : (double)MatchedCount / Total;
}

/// <summary>
/// Turns "the resume gains one more of this job's missing skills" into match points. Claude scored every job's skills (SkillsScore,
/// 40% of the match) from the same lists of matched and missing skills this analysis uses, so the user's own scored jobs show how
/// much one more skill is worth: a least-squares line is fitted to <c>SkillsScore ~ coverage</c> (coverage = matched / all the job asks for),
/// cross-validated, and its standard error and the correlation are computed (Math.NET Numerics). If that fit is trustworthy the slope is
/// used ("calibrated"); if there are too few jobs or the lists do not explain the scores, every missing skill is assumed to close an equal
/// share of the remaining gap.
/// <para>
/// A one-variable linear fit is solved exactly by least squares. An iterative trainer is the wrong tool here: ML.NET's SDCA was tried and, on
/// a few dozen points, diverged to non-finite weights, and its gradient-descent trainer came out biased low.
/// </para>
/// </summary>
internal sealed class UpliftModel
{
    /// <summary>Fewest scored jobs for which the fit is trusted at all.</summary>
    public const int MinSamples = 10;

    /// <summary>Lowest cross-validated R² at which the fit is trusted.</summary>
    public const double MinRSquared = 0.25;

    private UpliftModel(Calibration calibration, bool calibrated, double slope, double slopeSe, double residualSd)
    {
        Calibration = calibration;
        Calibrated = calibrated;
        Slope = slope;
        SlopeSe = slopeSe;
        ResidualSd = residualSd;
    }

    public Calibration Calibration { get; }
    public bool Calibrated { get; }

    /// <summary>Skills-score points per whole unit of coverage (so 100 would mean the score is exactly the coverage in percent).</summary>
    public double Slope { get; }

    public double SlopeSe { get; }

    /// <summary>How far a job's skills score is from the fitted line, typically (points).</summary>
    public double ResidualSd { get; }

    public static UpliftModel Fit(IReadOnlyList<JobFacts> jobs, int seed)
    {
        var usable = jobs.Where(j => j.Total > 0).ToList();
        var assumed = Assumed(usable.Count, "Fewer than " + MinSamples + " jobs have skill lists, so every missing skill is assumed to close an equal share of the gap in the skills score.");
        if (usable.Count < MinSamples) return assumed;

        var xs = usable.Select(j => j.Coverage).ToArray();
        var ys = usable.Select(j => (double)j.Job.SkillsScore).ToArray();
        if (xs.Max() - xs.Min() < 0.1)
            return Assumed(usable.Count, "The jobs ask for almost the same share of skills you have, so their scores say nothing about the value of one more skill; an equal share is assumed.");

        var correlation = Correlation.Pearson(xs, ys);

        double slope, intercept, cvR2;
        try
        {
            (slope, intercept, cvR2) = FitLine(xs, ys);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or ArithmeticException)
        {
            return Assumed(usable.Count, "The regression could not be fitted to these jobs; an equal share is assumed.");
        }

        if (slope <= 0 || cvR2 < MinRSquared)
        {
            var why = slope <= 0 ? "the scores do not rise with the share of skills covered" : $"the lists explain only {Math.Max(0, cvR2):P0} of the skills scores";
            return new UpliftModel(new Calibration("assumed", usable.Count, slope, intercept, Math.Max(0, cvR2), correlation,
                $"Claude's skills scores did not follow its own skill lists closely enough to learn from ({why}), so every missing skill is assumed to close an equal share of the gap."),
                calibrated: false, slope: 0, slopeSe: 0, residualSd: 0);
        }

        // residual spread and the standard error of the slope, in points (Math.NET)
        var residuals = xs.Select((x, i) => ys[i] - (intercept + slope * x)).ToArray();
        var sd = Math.Sqrt(residuals.Sum(r => r * r) / Math.Max(1, usable.Count - 2));
        var sxx = xs.Sum(x => Math.Pow(x - xs.Mean(), 2));
        var se = sxx > 0 ? sd / Math.Sqrt(sxx) : 0;

        return new UpliftModel(new Calibration("calibrated", usable.Count, slope, intercept, Math.Clamp(cvR2, 0, 1), correlation,
            $"Learned from your own {usable.Count} scored jobs: each extra skill a job asks for that the resume shows adds about {slope / 100:0.0} points to its skills score per 1% of coverage."),
            calibrated: true, slope, se, sd);
    }

    /// <summary>
    /// The least-squares line through the points, and how well a line fitted without a point predicts it: every point is predicted by a
    /// line fitted on the other folds (k-fold cross-validation), and R² is computed from those out-of-sample predictions.
    /// </summary>
    internal static (double Slope, double Intercept, double CvR2) FitLine(double[] xs, double[] ys)
    {
        var (intercept, slope) = MathNet.Numerics.Fit.Line(xs, ys);
        if (double.IsNaN(slope) || double.IsInfinity(slope)) throw new ArithmeticException("The line could not be fitted.");

        var folds = Math.Clamp(xs.Length / 4, 2, 5);
        var predicted = new double[xs.Length];
        for (var fold = 0; fold < folds; fold++)
        {
            var train = Enumerable.Range(0, xs.Length).Where(i => i % folds != fold).ToArray();
            var trainX = train.Select(i => xs[i]).ToArray();
            var trainY = train.Select(i => ys[i]).ToArray();

            // a training fold whose points all have the same x has no slope: it predicts its average
            var (a, b) = trainX.Max() - trainX.Min() < 1e-9 ? (trainY.Mean(), 0.0) : MathNet.Numerics.Fit.Line(trainX, trainY);
            for (var i = fold; i < xs.Length; i += folds) predicted[i] = a + b * xs[i];
        }

        var mean = ys.Mean();
        var total = ys.Sum(y => (y - mean) * (y - mean));
        var residual = ys.Select((y, i) => (y - predicted[i]) * (y - predicted[i])).Sum();
        return (slope, intercept, total > 0 ? 1 - residual / total : 0);
    }

    private static UpliftModel Assumed(int samples, string note) =>
        new(new Calibration("assumed", samples, 0, 0, 0, 0, note), calibrated: false, slope: 0, slopeSe: 0, residualSd: 0);

    /// <summary>
    /// The skills score of a job if the resume gained <paramref name="added"/> of the job's missing skills.
    /// <paramref name="slope"/> and <paramref name="share"/> default to the fitted values; the simulation passes sampled ones.
    /// </summary>
    public double SkillsScoreAfter(JobFacts job, int added, double? slope = null, double share = 1.0)
    {
        var score = (double)job.Job.SkillsScore;
        added = Math.Min(added, job.MissingKeys.Count);
        if (added <= 0 || job.MissingKeys.Count == 0) return score;

        var gain = Calibrated
            ? (slope ?? Slope) * added / Math.Max(1, job.Total)              // learned: points per unit of coverage
            : (100 - score) * added / job.MissingKeys.Count * share;         // assumed: an equal share of what is left
        return Math.Clamp(score + Math.Max(0, gain), 0, 100);
    }

    /// <summary>The job's match percentage with a given skills score, the other four dimensions unchanged (the app's own weighting).</summary>
    public static int PercentWith(AnalyzedJob job, double skillsScore) =>
        MatchResult.ComputePercent((int)Math.Round(skillsScore), job.ExperienceScore, job.DomainScore, job.SeniorityScore, job.LogisticsScore);

    public int PercentAfter(JobFacts job, int added, double? slope = null, double share = 1.0) =>
        added <= 0 ? job.Job.Percent : PercentWith(job.Job, SkillsScoreAfter(job, added, slope, share));
}
