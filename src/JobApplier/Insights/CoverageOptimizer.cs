using MathNet.Numerics.Distributions;
using MathNet.Numerics.Random;
using MathNet.Numerics.Statistics;

namespace JobApplier.Insights;

/// <summary>One step of the plan before it gets its display names.</summary>
internal sealed record PlanStep(string Key, int NewlyRelevant, int TotalRelevant, double AveragePoints, int JobsMissingIt, IReadOnlyList<(int Job, int From, int To)> Shifts);

/// <summary>
/// Chooses the few skills to add to the resume that make the most jobs relevant. This is a coverage problem (like the classic set cover):
/// a skill helps several jobs, a job needs several skills, and a job only counts once its match reaches the threshold. Finding the best
/// set exactly is NP-hard, so the plan is built greedily: each step takes the skill that pushes the most additional jobs over the threshold,
/// and when none does yet, the one that brings the jobs below it closest. That is a fast and sensible plan, not a proof of the best one.
/// The estimate of "how many points one skill is worth" comes from <see cref="UpliftModel"/>; <see cref="Simulate"/> then says how far
/// that estimate can be trusted.
/// </summary>
internal static class CoverageOptimizer
{
    public static IReadOnlyList<PlanStep> Plan(
        IReadOnlyList<JobFacts> jobs, IReadOnlyDictionary<string, List<int>> candidates, int threshold, int maxSteps, UpliftModel model)
    {
        var added = new int[jobs.Count];
        var current = jobs.Select(j => j.Job.Percent).ToArray();
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<PlanStep>();
        var relevant = current.Count(p => p >= threshold);

        for (var step = 0; step < maxSteps; step++)
        {
            (string Key, int Flips, double Progress, int Jobs)? best = null;

            foreach (var (key, indices) in candidates.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (chosen.Contains(key)) continue;

                var flips = 0;
                var progress = 0.0;
                foreach (var j in indices)
                {
                    var after = model.PercentAfter(jobs[j], added[j] + 1);
                    if (current[j] < threshold && after >= threshold) flips++;
                    progress += Math.Min(after, threshold) - Math.Min(current[j], threshold);   // progress towards relevant; beyond it nothing counts
                }

                if (best == null
                    || flips > best.Value.Flips
                    || (flips == best.Value.Flips && progress > best.Value.Progress + 1e-9)
                    || (flips == best.Value.Flips && Math.Abs(progress - best.Value.Progress) <= 1e-9 && indices.Count > best.Value.Jobs))
                    best = (key, flips, progress, indices.Count);
            }

            if (best == null || (best.Value.Flips == 0 && best.Value.Progress < 0.5)) break;   // nothing left that moves any job

            var shifts = new List<(int Job, int From, int To)>();
            double points = 0;
            foreach (var j in candidates[best.Value.Key])
            {
                var before = current[j];
                added[j]++;
                current[j] = model.PercentAfter(jobs[j], added[j]);
                points += current[j] - before;
                if (current[j] != before) shifts.Add((j, before, current[j]));
            }

            chosen.Add(best.Value.Key);
            relevant += best.Value.Flips;
            steps.Add(new PlanStep(best.Value.Key, best.Value.Flips, relevant, points / Math.Max(1, candidates[best.Value.Key].Count),
                candidates[best.Value.Key].Count,
                shifts.OrderByDescending(s => s.From < threshold && s.To >= threshold).ThenByDescending(s => s.To - s.From).Take(8).ToList()));
        }
        return steps;
    }

    /// <summary>
    /// How many jobs are relevant after the plan, as a range. The estimate of points per skill has a standard error, and Claude would
    /// re-score an edited resume with some noise; both are sampled (Math.NET distributions, a fixed seed so the range is reproducible)
    /// and the 10th and 90th percentile of the resulting counts are the range.
    /// </summary>
    public static Projection Simulate(
        IReadOnlyList<JobFacts> jobs, IReadOnlyList<string> plan, int threshold, int expected, UpliftModel model, int seed, int draws = 400)
    {
        var baseline = jobs.Count(j => j.Job.Percent >= threshold);
        if (plan.Count == 0) return new Projection(baseline, baseline, baseline, baseline, jobs.Count);

        var chosen = plan.ToHashSet(StringComparer.Ordinal);
        var addedCounts = jobs.Select(j => j.MissingKeys.Count(chosen.Contains)).ToArray();

        var rng = new MersenneTwister(seed);
        var standard = new Normal(0, 1, rng);
        var share = new ContinuousUniform(0.5, 1.0, rng);
        var noiseSd = Math.Sqrt(2) * model.ResidualSd;   // new score minus old score: each carries the same noise

        var counts = new double[draws];
        for (var d = 0; d < draws; d++)
        {
            var slope = model.Calibrated ? Math.Max(0, model.Slope + standard.Sample() * model.SlopeSe) : 0;
            var equalShare = model.Calibrated ? 1.0 : share.Sample();

            var relevant = 0;
            for (var j = 0; j < jobs.Count; j++)
            {
                if (addedCounts[j] == 0) { if (jobs[j].Job.Percent >= threshold) relevant++; continue; }

                var skills = model.SkillsScoreAfter(jobs[j], addedCounts[j], slope, equalShare);
                if (model.Calibrated) skills = Math.Clamp(skills + standard.Sample() * noiseSd, 0, 100);
                if (UpliftModel.PercentWith(jobs[j].Job, skills) >= threshold) relevant++;
            }
            counts[d] = relevant;
        }

        var low = (int)Math.Round(counts.Quantile(0.10));
        var high = (int)Math.Round(counts.Quantile(0.90));
        return new Projection(baseline, expected, Math.Min(low, expected), Math.Max(high, expected), jobs.Count);
    }
}
