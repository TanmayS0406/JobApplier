using System.Security.Cryptography;
using System.Text;
using MathNet.Numerics.Statistics;

namespace JobApplier.Insights;

/// <summary>
/// The resume gap analysis. Everything here runs on this PC and gives the same answer for the same inputs:
/// <list type="bullet">
/// <item>the skills the jobs asked for are merged (<see cref="SkillIndex"/>) and counted: which gaps recur;</item>
/// <item>a regression (Math.NET) learns from the user's own scored jobs how many points one more skill is worth (<see cref="UpliftModel"/>);</item>
/// <item>a greedy search (<see cref="CoverageOptimizer"/>) picks the few skills that make the most jobs relevant, and a Monte-Carlo
/// simulation (Math.NET) says how sure that is;</item>
/// <item>TF-IDF features of the posting texts (ML.NET) show which keywords the postings use and the resume never does, how alike the resume
/// and each posting are, and - with K-Means - which families of jobs there are and what each family lacks.</item>
/// </list>
/// Claude is not involved here: it comes in afterwards to say which gaps the resume already has evidence for and to word the changes.
/// </summary>
public static class GapAnalyzer
{
    private static readonly HashSet<string> GenericTerms = new(StringComparer.Ordinal)
    {
        "year", "day", "people", "business", "customer", "customers", "client", "clients", "project", "projects", "process", "processes",
        "support", "service", "services", "solution", "solutions", "environment", "ensure", "provide", "develop", "build", "create", "help",
        "using", "use", "make", "within", "part", "based", "key", "related", "like", "one", "two", "e", "g", "etc", "may", "get", "take",
        "equal opportunity", "years experience", "working experience",
        // words of any posting that say nothing about the work
        "group", "driven", "approved", "international", "native", "environments", "specifications", "global", "leading", "modern",
        // places: a resume cannot add a city
        "spain", "madrid", "barcelona", "germany", "berlin", "europe", "remote", "hybrid", "onsite", "office"
    };

    public static GapReport Analyze(string resumeText, IReadOnlyList<AnalyzedJob> jobs, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();
        resumeText ??= "";
        var threshold = options.Threshold;
        var fingerprint = Fingerprint(resumeText, jobs, options);
        var notes = new List<string>();

        if (jobs.Count == 0)
        {
            return new GapReport(threshold, 0, 0, 0, 0, Array.Empty<SkillGap>(), Array.Empty<Recommendation>(), new Projection(0, 0, 0, 0, 0),
                Array.Empty<JobFamily>(), Array.Empty<KeywordGap>(), new Calibration("assumed", 0, 0, 0, 0, 0, ""), null,
                new[] { "No job has been scored yet. Add some job screenshots first: the analysis learns from the match scores." }, fingerprint);
        }

        // ---- skills ----
        var index = SkillIndex.Build(jobs.SelectMany(j => j.Missing.Concat(j.Matched)));
        var facts = jobs.Select(j =>
        {
            // an entry such as "Docker and Kubernetes" holds two skills; each counts on its own
            var missing = j.Missing.SelectMany(index.KeysOf).ToHashSet(StringComparer.Ordinal);
            var matched = j.Matched.SelectMany(index.KeysOf).Where(k => !missing.Contains(k)).ToHashSet(StringComparer.Ordinal);
            return new JobFacts(j, missing, matched.Count);
        }).ToList();

        var resumeStems = SkillNormalizer.Tokens(resumeText).ToHashSet(StringComparer.Ordinal);
        bool InResume(string key) => resumeStems.Count > 0 && key.Split(' ').All(resumeStems.Contains);

        var jobsByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < facts.Count; i++)
            foreach (var key in facts[i].MissingKeys)
            {
                if (!jobsByKey.TryGetValue(key, out var list)) jobsByKey[key] = list = new List<int>();
                list.Add(i);
            }

        SkillGap GapOf(string key) => new(
            index.LabelOf(key), index.VariantsOf(key), jobsByKey[key].Count, jobsByKey[key].Count / (double)jobs.Count,
            jobsByKey[key].Average(i => facts[i].Job.Percent), InResume(key), SkillNormalizer.IsSoft(key), SkillNormalizer.IsEligibility(key));

        var gaps = jobsByKey.Keys.Select(GapOf).OrderByDescending(g => g.Jobs).ThenBy(g => g.Skill, StringComparer.OrdinalIgnoreCase).ToList();

        // ---- what one more skill is worth, and the plan ----
        var model = UpliftModel.Fit(facts, options.Seed);
        var minJobs = jobs.Count < 10 ? 1 : 2;
        var candidates = jobsByKey
            .Where(kv => kv.Value.Count >= minJobs && !SkillNormalizer.IsSoft(kv.Key) && !SkillNormalizer.IsEligibility(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var steps = CoverageOptimizer.Plan(facts, candidates, threshold, options.MaxRecommendations, model);

        var recommendations = steps.Select((s, i) => new Recommendation(
            i + 1, index.LabelOf(s.Key), index.VariantsOf(s.Key), s.NewlyRelevant, s.TotalRelevant, s.AveragePoints, s.JobsMissingIt, InResume(s.Key),
            s.Shifts.Select(x => new JobShift(jobs[x.Job].Id, jobs[x.Job].Company, jobs[x.Job].Title, x.From, x.To)).ToList())).ToList();

        var baseline = jobs.Count(j => j.Percent >= threshold);
        var expected = steps.Count == 0 ? baseline : steps[^1].TotalRelevant;
        var projection = CoverageOptimizer.Simulate(facts, steps.Select(s => s.Key).ToList(), threshold, expected, model, options.Seed);

        // ---- the posting texts ----
        // Only postings in the resume's own language are compared with it: a German posting against an English resume measures grammar words.
        var resumeLanguage = TextLanguage.Detect(resumeText) ?? "en";
        var withText = jobs.Select((job, i) => (Job: job, Index: i, Language: TextLanguage.Detect(job.Text))).Where(x => x.Job.Text.Length >= 200).ToList();
        var texted = withText.Where(x => (x.Language ?? resumeLanguage) == resumeLanguage).Select(x => (x.Job, x.Index)).ToList();
        var otherLanguages = withText.Where(x => (x.Language ?? resumeLanguage) != resumeLanguage).GroupBy(x => x.Language!).OrderByDescending(g => g.Count()).ToList();
        if (otherLanguages.Count > 0 && resumeText.Trim().Length > 0)
            notes.Add($"{otherLanguages.Sum(g => g.Count())} of the {withText.Count} posting texts are not in the language of your resume ({TextLanguage.Name(resumeLanguage)}): "
                      + string.Join(", ", otherLanguages.Select(g => $"{g.Count()} {TextLanguage.Name(g.Key)}"))
                      + ". They are left out of the keyword gaps, the job families and the text similarity; the skill analysis still counts them.");
        double? agreement = null;
        IReadOnlyList<KeywordGap> keywords = Array.Empty<KeywordGap>();
        IReadOnlyList<JobFamily> families = Array.Empty<JobFamily>();

        if (resumeText.Trim().Length == 0)
        {
            notes.Add("There is no resume text yet, so the comparison with the posting texts is skipped. Add your resume on the Profile page.");
        }
        else if (texted.Count < 6)
        {
            notes.Add($"Only {texted.Count} of the {jobs.Count} jobs have the posting text, so the keyword gaps and the job families are skipped (they need at least 6).");
        }
        else
        {
            var tfidf = TfIdfModel.Fit(texted.Select(x => x.Job.Text).ToList(), options.Seed);
            var resume = tfidf.Transform(resumeText);

            var similarity = tfidf.Documents.Select(d => SparseVector.Cosine(d, resume)).ToArray();
            var percents = texted.Select(x => (double)x.Job.Percent).ToArray();
            if (similarity.Max() - similarity.Min() > 1e-9 && percents.Max() - percents.Min() > 1e-9)
                agreement = Correlation.Pearson(similarity, percents);

            var companies = texted.Select(x => x.Job.Company).ToList();
            var termCompanies = CompanyCounts(tfidf, companies);
            keywords = KeywordGaps(tfidf, resume, resumeText, termCompanies, texted.Count);

            if (texted.Count >= 12)
            {
                families = Families(tfidf, texted.Select(x => x.Index).ToList(), facts, jobsByKey, GapOf, termCompanies, threshold, options.Seed, notes);
            }
            else
            {
                notes.Add($"Only {texted.Count} jobs have the posting text, so they are not grouped into families (that needs at least 12).");
            }
        }

        // ---- cautions ----
        if (jobs.Count < 8) notes.Add($"Only {jobs.Count} scored job{(jobs.Count == 1 ? "" : "s")}: treat this as a rough guide until you have scored more.");
        var withoutLists = jobs.Count(j => j.Missing.Count + j.Matched.Count == 0);
        if (withoutLists * 4 > jobs.Count) notes.Add($"{withoutLists} of the {jobs.Count} jobs have no skill lists, so they cannot tell which skills are missing.");
        if (baseline * 10 >= jobs.Count * 9) notes.Add("Almost every job already reaches the threshold; there is little left to gain from resume changes.");
        var eligibility = gaps.Where(g => g.Eligibility).Sum(g => g.Jobs);
        if (eligibility > 0)
            notes.Add($"{gaps.Count(g => g.Eligibility)} of the gaps are requirements about you (work authorisation, location, visa) rather than skills. A resume cannot add them, so they are kept out of the plan and listed separately.");
        if (gaps.Count == 0 && jobs.Count > 0) notes.Add("None of the scored jobs lists a missing skill.");

        return new GapReport(threshold, jobs.Count, withText.Count, baseline, jobs.Average(j => j.Percent), gaps, recommendations, projection,
            families, keywords, model.Calibration, agreement, notes, fingerprint);
    }

    /// <summary>
    /// Words and word pairs that many postings use and the resume never does, strongest first. A pair is listed when its words only ever
    /// occur together ("machine learning", which then replaces its two words) or when neither word is a keyword alone; a pair that merely
    /// puts a word the resume already has next to a new one ("docker kubernetes") is left out, because the new word says it.
    /// </summary>
    private static IReadOnlyList<KeywordGap> KeywordGaps(TfIdfModel tfidf, SparseVector resume, string resumeText, int[] termCompanies, int documents)
    {
        var minDocs = Math.Max(3, (int)Math.Ceiling(documents * 0.05));
        var minCompanies = Math.Min(3, Math.Max(1, termCompanies.Max()));   // a term must come from several employers, not one recruiter's repeated ad

        // "back-end" in the resume is "backend" in the posting: a term also counts as present when its word stems occur in the resume, or its letters do
        var resumeStems = SkillNormalizer.Tokens(resumeText).ToHashSet(StringComparer.Ordinal);
        var resumeLetters = SkillNormalizer.Prepare(resumeText).Replace(" ", "");
        bool InResume(string term) =>
            SkillNormalizer.Tokens(term).All(resumeStems.Contains) || resumeLetters.Contains(term.Replace(" ", ""), StringComparison.Ordinal);

        var terms = tfidf.Terms;
        var docCount = new int[terms.Count];
        var weight = new double[terms.Count];
        foreach (var doc in tfidf.Documents)
            for (var i = 0; i < doc.Indices.Length; i++)
            {
                docCount[doc.Indices[i]]++;
                weight[doc.Indices[i]] += doc.Values[i];
            }

        var docsOfTerm = Enumerable.Range(0, terms.Count).ToDictionary(t => terms[t], t => docCount[t], StringComparer.Ordinal);
        var resumeTerms = resume.Indices.Select(i => terms[i]).ToHashSet(StringComparer.Ordinal);

        var candidates = Enumerable.Range(0, terms.Count)
            .Where(t => docCount[t] >= minDocs && termCompanies[t] >= minCompanies && !resumeTerms.Contains(terms[t]) && !InResume(terms[t]))
            .Select(t => (Term: terms[t], Docs: docCount[t], Weight: weight[t]))
            .Where(t => t.Term.Length >= 3 && !GenericTerms.Contains(t.Term) && !t.Term.Split(' ').Any(GenericTerms.Contains))
            .ToList();

        var unigrams = candidates.Where(c => !c.Term.Contains(' ')).ToDictionary(c => c.Term, StringComparer.Ordinal);
        var kept = new List<(string Term, int Docs, double Weight)>();
        var swallowed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in candidates.Where(c => c.Term.Contains(' ')))
        {
            var words = pair.Term.Split(' ');
            if (words.Any(w => resumeTerms.Contains(w) || InResume(w))) continue;   // a word the resume has (however it spells it), next to a new one

            var onlyTogether = words.All(w => docsOfTerm.TryGetValue(w, out var d) && d <= pair.Docs * 1.25);
            if (onlyTogether)
            {
                kept.Add(pair);
                swallowed.UnionWith(words);
            }
            else if (words.All(w => !unigrams.ContainsKey(w)))
            {
                kept.Add(pair);
            }
        }
        kept.AddRange(unigrams.Values.Where(u => !swallowed.Contains(u.Term)));

        return kept.OrderByDescending(k => k.Weight).ThenBy(k => k.Term, StringComparer.Ordinal).Take(15)
            .Select(k => new KeywordGap(k.Term, k.Docs, k.Docs / (double)documents)).ToList();
    }

    /// <summary>For every term, from how many different employers' postings it comes.</summary>
    private static int[] CompanyCounts(TfIdfModel tfidf, IReadOnlyList<string> companies)
    {
        var sets = new HashSet<string>?[tfidf.Terms.Count];
        for (var d = 0; d < tfidf.Documents.Count; d++)
            foreach (var term in tfidf.Documents[d].Indices)
                (sets[term] ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(companies[d]);
        return sets.Select(set => set?.Count ?? 0).ToArray();
    }

    private static IReadOnlyList<JobFamily> Families(
        TfIdfModel tfidf, IReadOnlyList<int> jobIndexOfDoc, IReadOnlyList<JobFacts> facts, Dictionary<string, List<int>> jobsByKey,
        Func<string, SkillGap> gapOf, int[] termCompanies, int threshold, int seed, List<string> notes)
    {
        var minCompanies = Math.Min(3, Math.Max(1, termCompanies.Max()));
        var solution = JobClusterer.Solve(tfidf, Math.Min(6, jobIndexOfDoc.Count / 4), seed);
        if (solution == null)
        {
            notes.Add("The jobs are too alike (or too few) to split into distinct families.");
            return Array.Empty<JobFamily>();
        }

        var families = new List<JobFamily>();
        for (var c = 0; c < solution.K; c++)
        {
            var members = Enumerable.Range(0, jobIndexOfDoc.Count).Where(d => solution.Assignment[d] == c).Select(d => jobIndexOfDoc[d]).ToList();
            if (members.Count == 0) continue;

            var centroid = solution.Centroids[c];
            var topTerms = Enumerable.Range(0, centroid.Indices.Length)
                .OrderByDescending(i => centroid.Values[i])
                .Where(i => termCompanies[centroid.Indices[i]] >= minCompanies)   // not an employer's name
                .Select(i => tfidf.Terms[centroid.Indices[i]])
                .Where(t => t.Length >= 3 && !GenericTerms.Contains(t))
                .Take(6).ToList();

            var memberSet = members.ToHashSet();
            var topGaps = jobsByKey
                .Select(kv => (Key: kv.Key, Count: kv.Value.Count(memberSet.Contains)))
                .Where(x => x.Count > 0 && !SkillNormalizer.IsSoft(x.Key) && !SkillNormalizer.IsEligibility(x.Key))
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Take(3).Select(x => gapOf(x.Key)).ToList();

            families.Add(new JobFamily(c + 1, topTerms.Count == 0 ? "mixed" : string.Join(" · ", topTerms.Take(3)), topTerms, members.Count,
                members.Average(i => facts[i].Job.Percent), members.Count(i => facts[i].Job.Percent >= threshold), topGaps));
        }
        return families.OrderByDescending(f => f.Jobs).ToList();
    }

    /// <summary>Changes whenever anything the analysis (or the advice written from it) depends on changes.</summary>
    public static string Fingerprint(string resumeText, IReadOnlyList<AnalyzedJob> jobs, AnalysisOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(options.Threshold).Append('|').Append(options.MaxRecommendations).Append('|').Append(options.Seed).Append('|')
          .Append(string.Join(' ', (resumeText ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Append('\n');
        foreach (var job in jobs.OrderBy(j => j.Id))
        {
            sb.Append(job.Id).Append('|').Append(job.Percent).Append('|').Append(job.SkillsScore).Append(job.ExperienceScore).Append(job.DomainScore)
              .Append(job.SeniorityScore).Append(job.LogisticsScore).Append('|')
              .Append(string.Join(';', job.Missing)).Append('|').Append(string.Join(';', job.Matched)).Append('|').Append(job.Text.Length).Append('|')
              .Append(StableHash(job.Text)).Append('\n');
        }
        return StableHash(sb.ToString());
    }

    /// <summary>A hash that is the same in every run (string.GetHashCode is randomised per process, which would make saved advice look out of date after every restart).</summary>
    private static string StableHash(string text) =>
        text.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
