using JobApplier.Domain;
using JobApplier.Insights;

namespace JobApplier.Tests;

/// <summary>A small, fully known corpus: a backend/DevOps family and a data-science family, scored consistently with their skill lists.</summary>
internal static class InsightsData
{
    public const string Resume =
        "Backend engineer. Seven years building services in C# and .NET with SQL Server, Docker and REST APIs. Azure DevOps pipelines. " +
        "Led a small team and mentored juniors.";

    public static Guid IdOf(int n) => new(n, 0, 0, new byte[8]);

    public static AnalyzedJob Job(int n, string company, string title, int skills, IEnumerable<string> matched, IEnumerable<string> missing, string text, int others = 70) =>
        new(IdOf(n), company, title, MatchResult.ComputePercent(skills, others, others, others, others), skills, others, others, others, others,
            matched.ToList(), missing.ToList(), text);

    public static string BackendText(int i) =>
        $"We are hiring a backend engineer to build microservices on AWS using C# and .NET. You will deploy services with Docker and Kubernetes, " +
        $"manage infrastructure as code with Terraform and Helm, write SQL, and improve CI/CD pipelines. Join a collaborative platform team at Company{i} " +
        $"working on product {i % 3}.";

    public static string DataText(int i) =>
        $"We are hiring a data scientist to build machine learning models in Python with pandas and TensorFlow. You will analyse large datasets with SQL " +
        $"and statistics, run experiments and deploy models to production. Join a research focused analytics team at Lab{i} working on forecasting {i % 3}.";

    /// <summary>12 backend jobs (all miss Kubernetes, 8 also miss Terraform) and 10 data jobs (all miss Python and TensorFlow, 7 also Statistics).</summary>
    public static List<AnalyzedJob> Corpus()
    {
        var jobs = new List<AnalyzedJob>();
        for (var i = 0; i < 12; i++)
        {
            var missing = i < 8 ? new[] { "Kubernetes", "Terraform" } : new[] { "Kubernetes" };
            var coverage = 4.0 / (4 + missing.Length);
            jobs.Add(Job(i, $"Company{i}", "Backend Engineer", (int)Math.Round(coverage * 100) + (i % 3 - 1) * 2,
                new[] { "C#", ".NET", "SQL", "Docker" }, missing, BackendText(i)));
        }
        for (var i = 0; i < 10; i++)
        {
            var missing = i < 7 ? new[] { "Python", "TensorFlow", "Statistics" } : new[] { "Python", "TensorFlow" };
            var coverage = 1.0 / (1 + missing.Length);
            jobs.Add(Job(100 + i, $"Lab{i}", "Data Scientist", (int)Math.Round(coverage * 100) + (i % 3 - 1) * 2,
                new[] { "SQL" }, missing, DataText(i)));
        }
        return jobs;
    }
}

public class SkillNormalizerTests
{
    [Theory]
    [InlineData("Kubernetes", "K8s")]
    [InlineData("Kubernetes", "Experience with Kubernetes (K8s)")]
    [InlineData("Kubernetes", "kubernetes")]
    [InlineData("C#", "C# programming")]
    [InlineData("C#", "c#")]
    [InlineData(".NET", ".NET Core")]
    [InlineData(".NET", "ASP.NET Core")]
    [InlineData("REST API", "REST APIs")]
    [InlineData("REST API", "Experience with RESTful APIs")]
    [InlineData("Python", "5+ years of experience with Python")]
    [InlineData("Python", "Strong Python skills")]
    [InlineData("SQL", "SQL databases")]
    [InlineData("AWS", "Amazon Web Services")]
    [InlineData("Machine Learning", "machine-learning")]
    [InlineData("Postgres", "PostgreSQL")]
    public void Different_wordings_of_one_skill_have_one_key(string a, string b)
    {
        Assert.Equal(SkillNormalizer.Key(a), SkillNormalizer.Key(b));
        Assert.NotEqual("", SkillNormalizer.Key(a));
    }

    [Theory]
    [InlineData("Java", "JavaScript")]
    [InlineData("SQL", "NoSQL")]
    [InlineData("C", "C#")]
    [InlineData("Machine Learning", "Deep Learning")]
    [InlineData("Python", "Pythonic")]
    [InlineData("React", "React Native")]
    public void Different_skills_keep_different_keys(string a, string b)
    {
        Assert.NotEqual(SkillNormalizer.Key(a), SkillNormalizer.Key(b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Strong experience")]
    [InlineData("5+ years of")]
    [InlineData(null)]
    public void A_wording_that_is_only_filler_has_no_key(string? text)
    {
        Assert.Equal("", SkillNormalizer.Key(text));
    }

    [Fact]
    public void Word_order_does_not_matter()
    {
        Assert.Equal(SkillNormalizer.Key("cluster management Kubernetes"), SkillNormalizer.Key("Kubernetes cluster management"));
    }

    [Theory]
    [InlineData("Communication skills", true)]
    [InlineData("Strong problem-solving skills", true)]
    [InlineData("Teamwork", true)]
    [InlineData("Kubernetes", false)]
    [InlineData("Python", false)]
    [InlineData("English", false)]    // a language requirement can be met and stated on a resume, so it is not a "soft" trait
    public void General_traits_are_told_from_skills_a_resume_can_add(string skill, bool soft)
    {
        Assert.Equal(soft, SkillNormalizer.IsSoft(SkillNormalizer.Key(skill)));
    }

    [Fact]
    public void Text_for_the_model_keeps_technology_words_and_drops_numbers_and_punctuation()
    {
        var prepared = SkillNormalizer.Prepare("We use C# and .NET with K8s, 5 years! (CI/CD)");

        Assert.Contains("csharp", prepared);
        Assert.Contains("dotnet", prepared);
        Assert.Contains("kubernetes", prepared);
        Assert.Contains("cicd", prepared);
        Assert.DoesNotContain(" 5 ", " " + prepared + " ");
        Assert.DoesNotContain("!", prepared);
    }

    [Fact]
    public void A_wording_is_shown_on_one_line_and_not_longer_than_60_characters()
    {
        Assert.Equal("Kubernetes", SkillNormalizer.Display("  Kubernetes.  "));
        Assert.Equal("a b", SkillNormalizer.Display("a\n  b"));
        Assert.True(SkillNormalizer.Display(new string('x', 200)).Length <= 63);
    }

    [Fact]
    public void Accents_do_not_matter()
    {
        Assert.Equal(SkillNormalizer.Key("Gestion de proyectos"), SkillNormalizer.Key("Gestión de proyectos"));
    }
}

public class SkillIndexTests
{
    [Fact]
    public void Wordings_of_one_skill_become_one_entry_shown_under_the_most_common_wording()
    {
        var index = SkillIndex.Build(new[] { "Kubernetes", "Kubernetes", "K8s", "Experience with Kubernetes (K8s)", "Docker" });

        var key = index.KeyOf("K8s")!;
        Assert.Equal(key, index.KeyOf("Kubernetes"));
        Assert.Equal(key, index.KeyOf("experience with kubernetes (k8s)"));
        Assert.Equal("Kubernetes", index.LabelOf(key));
        Assert.Equal("Kubernetes", index.VariantsOf(key)[0]);
        Assert.Equal(3, index.VariantsOf(key).Count);
        Assert.Equal(2, index.Keys.Count);
    }

    [Fact]
    public void Among_equally_common_wordings_the_informative_one_leads()
    {
        var index = SkillIndex.Build(new[] { "K8s", "Kubernetes" });

        Assert.Equal("Kubernetes", index.LabelOf(index.KeyOf("K8s")!));   // K8s and Kubernetes are equally common: the full name leads
    }

    [Fact]
    public void A_typo_is_merged_with_the_skill_it_misspells()
    {
        var index = SkillIndex.Build(new[] { "Kubernetes", "Kubernetes", "Kubernets", "Docker" });

        Assert.Equal(index.KeyOf("Kubernetes"), index.KeyOf("Kubernets"));
        Assert.Equal("Kubernetes", index.LabelOf(index.KeyOf("Kubernets")!));
    }

    [Fact]
    public void Different_skills_are_never_merged_even_when_they_look_alike()
    {
        var index = SkillIndex.Build(new[] { "Java", "JavaScript", "TypeScript", "SQL", "NoSQL", "MySQL", "PostgreSQL", "React", "React Native" });

        Assert.Equal(9, index.Keys.Count);
    }

    [Fact]
    public void A_wording_that_is_only_filler_belongs_to_no_skill()
    {
        var index = SkillIndex.Build(new[] { "Strong experience", "Python" });

        Assert.Null(index.KeyOf("Strong experience"));
        Assert.Null(index.KeyOf(null));
        Assert.Single(index.Keys);
    }
}

public class TextAnalyticsTests
{
    private static TfIdfModel Fit() =>
        TfIdfModel.Fit(InsightsData.Corpus().Select(j => j.Text).ToList(), seed: 3);

    [Fact]
    public void The_vocabulary_holds_the_technology_words_and_pairs_of_the_postings()
    {
        var model = Fit();

        Assert.Contains("kubernetes", model.Terms);
        Assert.Contains("tensorflow", model.Terms);
        Assert.Contains("csharp", model.Terms);
        Assert.Contains("machine learning", model.Terms);   // a pair of words
        Assert.Equal(22, model.Documents.Count);
    }

    [Fact]
    public void Common_words_of_english_and_spanish_and_the_filler_of_job_ads_are_left_out()
    {
        var model = TfIdfModel.Fit(new[]
        {
            "We are looking for a team player with strong experience to join our company and work with Kubernetes in the cloud",
            "Buscamos un equipo con experiencia para trabajar con Kubernetes en la nube de nuestra empresa",
            "The role requires Terraform and the ability to work with the team",
            "Para el puesto se necesita Terraform y trabajo en equipo con los demás"
        }, 3);

        foreach (var common in new[] { "the", "and", "with", "team", "experience", "para", "con", "los", "equipo", "empresa", "company", "work" })
            Assert.DoesNotContain(model.Terms, t => t.Split(' ').Contains(common));
        Assert.Contains("kubernetes", model.Terms);
        Assert.Contains("terraform", model.Terms);
    }

    [Fact]
    public void Document_vectors_have_length_one_so_a_dot_product_is_a_cosine()
    {
        foreach (var document in Fit().Documents)
            Assert.Equal(1.0, document.Norm, 3);
    }

    [Fact]
    public void A_resume_is_closer_to_the_postings_that_share_its_words()
    {
        var model = Fit();
        var resume = model.Transform(InsightsData.Resume);

        var backend = model.Documents.Take(12).Average(d => SparseVector.Cosine(d, resume));
        var data = model.Documents.Skip(12).Average(d => SparseVector.Cosine(d, resume));

        Assert.True(backend > data * 1.5, $"backend {backend:0.000} vs data {data:0.000}");
    }

    [Fact]
    public void Words_the_postings_never_used_are_dropped_from_a_new_text()
    {
        var model = Fit();

        Assert.Empty(model.Transform("xylophone zeppelin quokka").Indices);
        Assert.NotEmpty(model.Transform("Kubernetes and Terraform").Indices);
    }

    [Fact]
    public void The_same_postings_always_give_the_same_vectors()
    {
        var first = Fit();
        var second = Fit();

        Assert.Equal(first.Terms, second.Terms);
        Assert.Equal(first.Documents[0].Values, second.Documents[0].Values);
    }
}

public class JobClusteringTests
{
    [Fact]
    public void Two_different_kinds_of_postings_fall_into_two_families()
    {
        var corpus = InsightsData.Corpus();
        var model = TfIdfModel.Fit(corpus.Select(j => j.Text).ToList(), 3);

        var solution = JobClusterer.Solve(model, maxK: 5, seed: 3);

        Assert.NotNull(solution);
        Assert.Equal(2, solution!.K);
        Assert.Single(solution.Assignment.Take(12).Distinct());      // all 12 backend postings together
        Assert.Single(solution.Assignment.Skip(12).Distinct());      // all 10 data postings together
        Assert.NotEqual(solution.Assignment[0], solution.Assignment[12]);
    }

    [Fact]
    public void Too_few_postings_are_not_split()
    {
        var model = TfIdfModel.Fit(InsightsData.Corpus().Take(4).Select(j => j.Text).ToList(), 3);

        Assert.Null(JobClusterer.Solve(model, maxK: 5, seed: 3));
    }

    [Fact]
    public void The_grouping_is_reproducible()
    {
        var model = TfIdfModel.Fit(InsightsData.Corpus().Select(j => j.Text).ToList(), 3);

        Assert.Equal(JobClusterer.Solve(model, 5, 3)!.Assignment, JobClusterer.Solve(model, 5, 3)!.Assignment);
    }
}

public class UpliftModelTests
{
    private static JobFacts Facts(int n, int matched, int missing, int skills, int others = 60)
    {
        var job = InsightsData.Job(n, "Co" + n, "Dev", skills, Enumerable.Range(0, matched).Select(i => "m" + i), Enumerable.Range(0, missing).Select(i => "x" + i), "", others);
        return new JobFacts(job, Enumerable.Range(0, missing).Select(i => "x" + i).ToHashSet(), matched);
    }

    /// <summary>Jobs whose skills score is exactly the share of skills covered, in percent.</summary>
    private static List<JobFacts> Consistent(int count)
    {
        var facts = new List<JobFacts>();
        for (var i = 0; i < count; i++)
        {
            var total = 4 + i % 5;
            var matched = i % (total + 1);
            facts.Add(Facts(i, matched, total - matched, (int)Math.Round(100.0 * matched / total)));
        }
        return facts;
    }

    [Fact]
    public void When_the_scores_follow_the_skill_lists_the_slope_is_learned_from_the_jobs()
    {
        var model = UpliftModel.Fit(Consistent(40), seed: 5);

        Assert.True(model.Calibrated, model.Calibration.Note);
        Assert.Equal("calibrated", model.Calibration.Method);
        Assert.InRange(model.Slope, 90, 110);                       // a score equal to the coverage in percent: 100 points per unit
        Assert.True(model.Calibration.RSquared > 0.9, $"R2 {model.Calibration.RSquared}");
        Assert.True(model.Calibration.Correlation > 0.95);
        Assert.Equal(40, model.Calibration.Samples);
    }

    [Fact]
    public void Scores_unrelated_to_the_skill_lists_are_not_trusted()
    {
        var rng = new Random(11);
        var facts = Consistent(40).Select(f => Facts(f.Job.Id.GetHashCode() & 0xFFFF, f.MatchedCount, f.MissingKeys.Count, rng.Next(20, 90))).ToList();

        var model = UpliftModel.Fit(facts, seed: 5);

        Assert.False(model.Calibrated);
        Assert.Equal("assumed", model.Calibration.Method);
        Assert.Contains("equal share", model.Calibration.Note);
    }

    [Fact]
    public void With_too_few_jobs_an_equal_share_is_assumed()
    {
        var model = UpliftModel.Fit(Consistent(UpliftModel.MinSamples - 1), 5);

        Assert.False(model.Calibrated);
        Assert.Contains("Fewer than", model.Calibration.Note);
    }

    [Fact]
    public void Scores_that_do_not_vary_with_coverage_cannot_teach_anything()
    {
        var facts = Enumerable.Range(0, 20).Select(i => Facts(i, 3, 3, 40 + i % 3)).ToList();   // every job covers exactly half

        Assert.False(UpliftModel.Fit(facts, 5).Calibrated);
    }

    [Fact]
    public void An_assumed_skill_closes_an_equal_share_of_what_is_left()
    {
        var model = UpliftModel.Fit(Consistent(3), 5);   // too few: assumed
        var job = Facts(1, 1, 2, 50);

        Assert.Equal(50, model.SkillsScoreAfter(job, 0), 6);
        Assert.Equal(75, model.SkillsScoreAfter(job, 1), 6);     // one of two missing skills: half of the remaining 50 points
        Assert.Equal(100, model.SkillsScoreAfter(job, 2), 6);
        Assert.Equal(100, model.SkillsScoreAfter(job, 9), 6);    // never more than the job asks for
    }

    [Fact]
    public void A_calibrated_skill_is_worth_the_slope_times_its_share_of_the_jobs_skills()
    {
        var model = UpliftModel.Fit(Consistent(40), 5);
        var job = Facts(1, 2, 2, 50);   // 4 skills: one more is a quarter of the coverage

        var gain = model.SkillsScoreAfter(job, 1) - 50;

        Assert.Equal(model.Slope / 4, gain, 6);
        Assert.Equal(100, model.SkillsScoreAfter(Facts(2, 1, 1, 95), 1, slope: 400), 6);   // clamped at 100
    }

    [Fact]
    public void The_percentage_uses_the_apps_own_weighting_with_the_other_four_scores_unchanged()
    {
        var job = InsightsData.Job(1, "Co", "Dev", 50, Array.Empty<string>(), Array.Empty<string>(), "", others: 60);

        Assert.Equal(MatchResult.ComputePercent(80, 60, 60, 60, 60), UpliftModel.PercentWith(job, 80));
        Assert.Equal(68, UpliftModel.PercentWith(job, 80));   // 0.4 * 80 + 0.6 * 60
    }

    [Fact]
    public void A_job_with_nothing_missing_has_nothing_to_gain()
    {
        var model = UpliftModel.Fit(Consistent(40), 5);
        var job = Facts(1, 4, 0, 100);

        Assert.Equal(100, model.SkillsScoreAfter(job, 1), 6);
        Assert.Equal(job.Job.Percent, model.PercentAfter(job, 0));
    }
}

public class CoverageOptimizerTests
{
    private static JobFacts Job(int n, int skills, params string[] missing)
    {
        var job = InsightsData.Job(n, "Co" + n, "Dev", skills, new[] { "m" }, missing, "", others: 60);
        return new JobFacts(job, missing.ToHashSet(), matchedCount: 1);
    }

    private static Dictionary<string, List<int>> Candidates(IReadOnlyList<JobFacts> jobs)
    {
        var map = new Dictionary<string, List<int>>();
        for (var i = 0; i < jobs.Count; i++)
            foreach (var key in jobs[i].MissingKeys)
            {
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<int>();
                list.Add(i);
            }
        return map;
    }

    private static UpliftModel Assumed() => UpliftModel.Fit(new[] { Job(0, 50, "a") }, 1);   // one job: an equal share is assumed

    [Fact]
    public void The_plan_builds_up_to_jobs_that_need_several_skills_and_counts_them_when_they_cross_the_threshold()
    {
        // each job needs two of a, b, c; one skill alone never reaches 70 (0.4 * 75 + 36 = 66), two do (76)
        var jobs = new[] { Job(1, 50, "a", "b"), Job(2, 50, "a", "c"), Job(3, 50, "b", "c") };

        var plan = CoverageOptimizer.Plan(jobs, Candidates(jobs), threshold: 70, maxSteps: 5, Assumed());

        Assert.Equal(new[] { "a", "b", "c" }, plan.Select(s => s.Key));
        Assert.Equal(new[] { 0, 1, 2 }, plan.Select(s => s.NewlyRelevant));
        Assert.Equal(new[] { 0, 1, 3 }, plan.Select(s => s.TotalRelevant));
    }

    [Fact]
    public void The_skill_that_makes_the_most_jobs_relevant_comes_first()
    {
        // x alone lifts four jobs over the line; y alone lifts one
        var jobs = new[] { Job(1, 60, "x"), Job(2, 60, "x"), Job(3, 60, "x"), Job(4, 60, "x"), Job(5, 60, "y") };

        var plan = CoverageOptimizer.Plan(jobs, Candidates(jobs), 70, 5, Assumed());

        Assert.Equal("x", plan[0].Key);
        Assert.Equal(4, plan[0].NewlyRelevant);
        Assert.Equal("y", plan[1].Key);
        Assert.Equal(5, plan[1].TotalRelevant);
        Assert.Equal(4, plan[0].JobsMissingIt);
    }

    [Fact]
    public void Jobs_that_are_already_relevant_do_not_attract_skills()
    {
        // job 1 passes already; only job 2 is below the line, and only "q" can lift it
        var jobs = new[] { Job(1, 95, "p"), Job(2, 60, "q") };

        var plan = CoverageOptimizer.Plan(jobs, Candidates(jobs), 70, 5, Assumed());

        Assert.Equal("q", plan[0].Key);
        Assert.DoesNotContain(plan, s => s.Key == "p");   // adding p would only push job 1 further past a line it has already crossed
    }

    [Fact]
    public void The_plan_stops_when_nothing_moves_any_more_and_never_exceeds_the_limit()
    {
        var jobs = Enumerable.Range(0, 6).Select(i => Job(i, 40, "s" + i)).ToArray();

        Assert.True(CoverageOptimizer.Plan(jobs, Candidates(jobs), 70, 3, Assumed()).Count <= 3);
        Assert.Empty(CoverageOptimizer.Plan(new[] { Job(1, 100) }, new Dictionary<string, List<int>>(), 70, 5, Assumed()));
    }

    [Fact]
    public void A_step_lists_the_jobs_it_moves_with_the_ones_that_cross_the_line_first()
    {
        // x is job 1's only missing skill (so it closes the whole gap: 52 -> 76) but only one of job 2's two (so it gains, without crossing)
        var jobs = new[] { Job(1, 60, "x"), Job(2, 20, "x", "y") };

        var step = CoverageOptimizer.Plan(jobs, Candidates(jobs), 70, 1, Assumed())[0];

        Assert.Equal(InsightsData.IdOf(1), jobs[step.Shifts[0].Job].Job.Id);   // job 1 crosses; job 2 only gains
        Assert.True(step.Shifts[0].To >= 70 && step.Shifts[0].From < 70);
        Assert.Equal(2, step.Shifts.Count);
    }

    [Fact]
    public void The_plan_is_the_same_every_time()
    {
        var jobs = Enumerable.Range(0, 12).Select(i => Job(i, 50 + i % 4 * 3, "a" + i % 3, "b" + i % 4)).ToArray();

        var first = CoverageOptimizer.Plan(jobs, Candidates(jobs), 70, 6, Assumed()).Select(s => s.Key + s.TotalRelevant);
        var second = CoverageOptimizer.Plan(jobs, Candidates(jobs), 70, 6, Assumed()).Select(s => s.Key + s.TotalRelevant);

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_range_always_contains_the_expected_count_and_is_reproducible()
    {
        var jobs = Enumerable.Range(0, 20).Select(i => Job(i, 55 + i % 5 * 2, "k", "t" + i % 2)).ToArray();
        var model = Assumed();
        var plan = CoverageOptimizer.Plan(jobs, Candidates(jobs), 70, 4, model);

        var projection = CoverageOptimizer.Simulate(jobs, plan.Select(p => p.Key).ToList(), 70, plan[^1].TotalRelevant, model, seed: 9);
        var again = CoverageOptimizer.Simulate(jobs, plan.Select(p => p.Key).ToList(), 70, plan[^1].TotalRelevant, model, seed: 9);

        Assert.True(projection.Low <= projection.Expected && projection.Expected <= projection.High);
        Assert.InRange(projection.Baseline, 0, projection.Total);
        Assert.Equal(projection, again);
        Assert.Equal(20, projection.Total);
    }

    [Fact]
    public void With_no_plan_the_projection_is_the_baseline()
    {
        var jobs = new[] { Job(1, 95), Job(2, 20) };

        var projection = CoverageOptimizer.Simulate(jobs, Array.Empty<string>(), 70, 1, Assumed(), 1);

        Assert.Equal(new Projection(1, 1, 1, 1, 2), projection);
    }
}

public class GapAnalyzerTests
{
    private static GapReport Analyze(IReadOnlyList<AnalyzedJob>? jobs = null, int threshold = 70) =>
        GapAnalyzer.Analyze(InsightsData.Resume, jobs ?? InsightsData.Corpus(), new AnalysisOptions(threshold));

    [Fact]
    public void The_recurring_gaps_are_counted_across_the_jobs_with_the_wordings_merged()
    {
        var report = Analyze();

        Assert.Equal(22, report.ScoredJobs);
        var kubernetes = report.Gaps.Single(g => g.Skill == "Kubernetes");
        Assert.Equal(12, kubernetes.Jobs);
        Assert.Equal(12 / 22.0, kubernetes.Share, 6);
        Assert.Equal(10, report.Gaps.Single(g => g.Skill == "Python").Jobs);
        Assert.Equal(8, report.Gaps.Single(g => g.Skill == "Terraform").Jobs);
        Assert.Equal(report.Gaps.OrderByDescending(g => g.Jobs).First().Jobs, report.Gaps[0].Jobs);   // the biggest gap first
    }

    [Fact]
    public void The_first_recommendation_is_the_skill_that_lifts_the_most_jobs()
    {
        var report = Analyze();

        var first = report.Recommendations[0];
        Assert.Equal("Kubernetes", first.Skill);
        Assert.True(first.NewlyRelevant >= 6, $"newly relevant {first.NewlyRelevant}");
        Assert.True(first.TotalRelevant > report.RelevantNow);
        Assert.Equal(1, first.Rank);
        Assert.All(first.Shifts, s => Assert.True(s.To >= s.From));
    }

    [Fact]
    public void Both_families_get_their_own_recommendations()
    {
        var report = Analyze();

        var skills = report.Recommendations.Select(r => r.Skill).ToList();
        Assert.Contains("Python", skills);
        Assert.Contains("TensorFlow", skills);
        Assert.True(report.Recommendations[^1].TotalRelevant >= report.RelevantNow + 12, $"{report.RelevantNow} -> {report.Recommendations[^1].TotalRelevant}");
        // cumulative counts never go down
        Assert.Equal(report.Recommendations.Select(r => r.TotalRelevant).OrderBy(x => x), report.Recommendations.Select(r => r.TotalRelevant));
    }

    [Fact]
    public void The_projection_has_an_honest_range_around_the_plans_outcome()
    {
        var report = Analyze();

        Assert.Equal(report.RelevantNow, report.Projection.Baseline);
        Assert.Equal(report.Recommendations[^1].TotalRelevant, report.Projection.Expected);
        Assert.True(report.Projection.Low <= report.Projection.Expected && report.Projection.Expected <= report.Projection.High);
        Assert.Equal(22, report.Projection.Total);
    }

    [Fact]
    public void The_calibration_is_learned_from_the_scored_jobs_when_they_are_consistent()
    {
        var report = Analyze();

        Assert.Equal("calibrated", report.Calibration.Method);
        Assert.InRange(report.Calibration.Slope, 70, 130);
        Assert.True(report.Calibration.Samples == 22);
    }

    [Fact]
    public void A_skill_whose_words_are_already_in_the_resume_is_flagged_as_present_but_not_credited()
    {
        var jobs = InsightsData.Corpus().Select(j => j with { Missing = j.Missing.Concat(new[] { "REST APIs" }).ToList() }).ToList();
        // the resume says "REST APIs" and the postings still list it as missing: the words are there, so it is probably buried or phrased differently

        var gap = Analyze(jobs).Gaps.Single(g => g.Skill == "REST APIs");

        Assert.True(gap.InResumeText);
        Assert.False(Analyze().Gaps.Single(g => g.Skill == "Kubernetes").InResumeText);
    }

    [Fact]
    public void Keywords_the_postings_use_and_the_resume_never_does_are_listed()
    {
        var report = Analyze();

        var terms = report.Keywords.Select(k => k.Term).ToList();
        Assert.Contains(terms, t => t.Split(' ').Contains("kubernetes"));
        Assert.Contains(terms, t => t.Split(' ').Contains("terraform"));
        Assert.DoesNotContain(terms, t => t.Split(' ').Contains("docker"));     // the resume has it
        Assert.DoesNotContain(terms, t => t.Split(' ').Contains("csharp"));
        Assert.All(report.Keywords, k => Assert.InRange(k.Jobs, 3, 22));
        Assert.True(report.Keywords.Count <= 15);
    }

    [Fact]
    public void A_word_that_is_only_part_of_a_listed_pair_is_not_listed_on_its_own()
    {
        var terms = Analyze().Keywords.Select(k => k.Term).ToList();

        foreach (var pair in terms.Where(t => t.Contains(' ')))
            foreach (var word in pair.Split(' '))
                Assert.DoesNotContain(word, terms);
    }

    [Fact]
    public void The_jobs_fall_into_families_with_their_own_gaps_and_numbers()
    {
        var report = Analyze();

        Assert.Equal(2, report.Families.Count);
        Assert.Equal(new[] { 12, 10 }, report.Families.Select(f => f.Jobs));
        var backend = report.Families[0];
        Assert.Contains(backend.TopGaps, g => g.Skill == "Kubernetes");
        Assert.Contains(report.Families[1].TopGaps, g => g.Skill == "Python");
        Assert.Contains(backend.TopTerms, t => t.Contains("backend") || t.Contains("kubernetes") || t.Contains("microservices") || t.Contains("terraform"));
        Assert.Contains(report.Families[1].TopTerms, t => t.Contains("data") || t.Contains("tensorflow") || t.Contains("learning"));
        Assert.Equal(22, report.Families.Sum(f => f.Jobs));
        Assert.Equal(report.RelevantNow, report.Families.Sum(f => f.Relevant));
    }

    [Fact]
    public void The_text_similarity_agrees_with_the_scores_when_the_scores_are_sensible()
    {
        var report = Analyze();

        Assert.NotNull(report.TextAgreement);
        Assert.True(report.TextAgreement > 0.5, $"r = {report.TextAgreement}");
    }

    [Fact]
    public void Without_jobs_the_report_says_what_to_do_first()
    {
        var report = GapAnalyzer.Analyze(InsightsData.Resume, Array.Empty<AnalyzedJob>());

        Assert.False(report.HasData);
        Assert.Empty(report.Recommendations);
        Assert.Contains(report.Notes, n => n.Contains("screenshots"));
    }

    [Fact]
    public void Few_jobs_give_a_caution_and_no_families()
    {
        var report = Analyze(InsightsData.Corpus().Take(5).ToList());

        Assert.Contains(report.Notes, n => n.Contains("Only 5 scored jobs"));
        Assert.Empty(report.Families);
        Assert.Equal("assumed", report.Calibration.Method);
    }

    [Fact]
    public void Jobs_without_posting_text_still_give_the_skill_analysis_but_skip_the_text_analysis()
    {
        var jobs = InsightsData.Corpus().Select(j => j with { Text = "" }).ToList();

        var report = Analyze(jobs);

        Assert.NotEmpty(report.Recommendations);
        Assert.Empty(report.Keywords);
        Assert.Empty(report.Families);
        Assert.Null(report.TextAgreement);
        Assert.Equal(0, report.JobsWithText);
        Assert.Contains(report.Notes, n => n.Contains("posting text"));
    }

    [Fact]
    public void An_empty_resume_skips_the_comparison_instead_of_calling_every_word_a_gap()
    {
        var report = GapAnalyzer.Analyze("", InsightsData.Corpus());

        Assert.Empty(report.Keywords);
        Assert.Contains(report.Notes, n => n.Contains("no resume text"));
        Assert.NotEmpty(report.Recommendations);
    }

    [Fact]
    public void Soft_skills_are_listed_as_gaps_but_never_recommended()
    {
        var jobs = InsightsData.Corpus().Select(j => j with { Missing = j.Missing.Concat(new[] { "Strong communication skills" }).ToList() }).ToList();

        var report = Analyze(jobs);

        var gap = report.Gaps.Single(g => g.Soft);
        Assert.Equal(22, gap.Jobs);
        Assert.DoesNotContain(report.Recommendations, r => r.Skill.Contains("communication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_higher_threshold_makes_fewer_jobs_relevant_to_begin_with()
    {
        Assert.True(Analyze(threshold: 80).RelevantNow <= Analyze(threshold: 60).RelevantNow);
    }

    [Fact]
    public void The_analysis_is_deterministic()
    {
        var first = Analyze();
        var second = Analyze();

        Assert.Equal(first.Recommendations.Select(r => (r.Skill, r.NewlyRelevant, r.TotalRelevant)), second.Recommendations.Select(r => (r.Skill, r.NewlyRelevant, r.TotalRelevant)));
        Assert.Equal(first.Projection, second.Projection);
        Assert.Equal(first.Keywords.Select(k => k.Term), second.Keywords.Select(k => k.Term));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void The_fingerprint_changes_with_the_resume_the_jobs_and_the_threshold_but_not_between_runs()
    {
        var jobs = InsightsData.Corpus();
        var options = new AnalysisOptions(70);
        var baseline = GapAnalyzer.Fingerprint(InsightsData.Resume, jobs, options);

        Assert.Equal(baseline, GapAnalyzer.Fingerprint(InsightsData.Resume, InsightsData.Corpus(), options));
        Assert.NotEqual(baseline, GapAnalyzer.Fingerprint(InsightsData.Resume + " Added Kubernetes.", jobs, options));
        Assert.NotEqual(baseline, GapAnalyzer.Fingerprint(InsightsData.Resume, jobs.Skip(1).ToList(), options));
        Assert.NotEqual(baseline, GapAnalyzer.Fingerprint(InsightsData.Resume, jobs, new AnalysisOptions(80)));
        Assert.NotEqual(baseline, GapAnalyzer.Fingerprint(InsightsData.Resume, jobs.Select(j => j with { Missing = j.Missing.Append("Go").ToList() }).ToList(), options));
    }

    [Fact]
    public void Almost_every_job_already_passing_is_said_plainly()
    {
        var jobs = Enumerable.Range(0, 10).Select(i => InsightsData.Job(i, "Co" + i, "Dev", 95, new[] { "C#" }, new[] { "Go" }, "")).ToList();

        Assert.Contains(GapAnalyzer.Analyze(InsightsData.Resume, jobs).Notes, n => n.Contains("Almost every job"));
    }
}
