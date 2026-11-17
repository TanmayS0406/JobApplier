using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Text;

namespace JobApplier.Insights;

/// <summary>A sparse vector with ascending indices.</summary>
internal readonly record struct SparseVector(int[] Indices, float[] Values)
{
    public static SparseVector Empty { get; } = new(Array.Empty<int>(), Array.Empty<float>());

    public static double Dot(SparseVector a, SparseVector b)
    {
        double sum = 0;
        int i = 0, j = 0;
        while (i < a.Indices.Length && j < b.Indices.Length)
        {
            if (a.Indices[i] == b.Indices[j]) sum += (double)a.Values[i++] * b.Values[j++];
            else if (a.Indices[i] < b.Indices[j]) i++;
            else j++;
        }
        return sum;
    }

    public double Norm => Math.Sqrt(Values.Sum(v => (double)v * v));

    public static double Cosine(SparseVector a, SparseVector b)
    {
        var norm = a.Norm * b.Norm;
        return norm == 0 ? 0 : Dot(a, b) / norm;
    }

    /// <summary>The mean of some vectors.</summary>
    public static SparseVector Mean(IReadOnlyList<SparseVector> vectors)
    {
        if (vectors.Count == 0) return Empty;
        var sum = new SortedDictionary<int, double>();
        foreach (var v in vectors)
            for (var i = 0; i < v.Indices.Length; i++)
                sum[v.Indices[i]] = sum.GetValueOrDefault(v.Indices[i]) + v.Values[i];
        return new SparseVector(sum.Keys.ToArray(), sum.Values.Select(x => (float)(x / vectors.Count)).ToArray());
    }

    /// <summary>The distance between two vectors.</summary>
    public static double Distance(SparseVector a, SparseVector b) =>
        Math.Sqrt(Math.Max(0, a.Values.Sum(v => (double)v * v) + b.Values.Sum(v => (double)v * v) - 2 * Dot(a, b)));
}

/// <summary>
/// TF-IDF features of a set of postings, made with ML.NET: words and word pairs (n-grams of length 1 and 2), common words of English,
/// Spanish and German and the filler of job ads removed, each document's vector scaled to length 1 so that a dot product is a cosine
/// similarity. Fitting learns the vocabulary and the inverse document frequencies from the postings; <see cref="Transform"/> then expresses
/// any other text (the resume) in the same vocabulary.
/// </summary>
internal sealed class TfIdfModel
{
    private sealed class Doc
    {
        public string Text { get; set; } = "";
    }

    /// <summary>How many different words and pairs are kept (the most frequent ones). A bigger vocabulary only adds rare words and makes clustering slow.</summary>
    private const int MaxTerms = 3000;

    /// <summary>Words that carry no meaning for a comparison between job ads: grammar words in Spanish and German, and the vocabulary every ad shares.</summary>
    private static readonly string[] ExtraStopWords =
    {
        // Spanish grammar
        "de", "la", "el", "en", "y", "a", "los", "las", "del", "se", "con", "para", "por", "un", "una", "que", "es", "al", "lo", "como", "mas",
        "o", "su", "sus", "este", "esta", "ser", "son", "tambien", "entre", "sobre", "desde", "hasta", "nuestro", "nuestra", "nuestros", "te",
        "tu", "tus", "nos", "si", "no", "muy", "ya", "pero", "sin", "u", "e", "le", "les", "me", "mi", "estamos", "buscamos", "eres",
        // German grammar
        "und", "der", "die", "das", "mit", "fur", "von", "zu", "den", "im", "ist", "des", "dem", "ein", "eine", "auf", "bei", "sie", "wir", "als",
        "auch", "nicht", "sich", "oder", "werden", "unsere", "ihre", "einen", "einem", "zum", "zur", "uns", "ihr", "sind", "wird", "ab",
        // what every job ad says
        "experience", "work", "working", "team", "teams", "role", "job", "company", "years", "year", "ability", "skills", "skill", "strong",
        "responsibilities", "responsibility", "requirements", "required", "benefits", "join", "looking", "opportunity", "candidate",
        "candidates", "position", "etc", "equal", "employer", "salary", "apply", "application", "will", "must", "within", "across", "well",
        "including", "also", "new", "us", "our", "we", "you", "your", "are", "have", "has", "be", "can", "good", "great", "excellent",
        "knowledge", "understanding", "preferred", "plus", "least", "per", "ideal", "ideally", "needed", "need", "high", "level",
        "experiencia", "equipo", "trabajo", "puesto", "empresa", "anos", "funciones", "requisitos", "oferta", "ofrecemos", "incorporacion",
        "profesional", "conocimientos", "valorable", "valorables", "imprescindible", "deseable"
    };

    private readonly MLContext _ml;
    private readonly ITransformer _model;

    private TfIdfModel(MLContext ml, ITransformer model, IDataView transformed, IReadOnlyList<string> terms, IReadOnlyList<SparseVector> documents)
    {
        _ml = ml;
        _model = model;
        Transformed = transformed;
        Terms = terms;
        Documents = documents;
    }

    /// <summary>The vocabulary: the word or word pair behind every index of a vector (a pair is written "machine learning").</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>One L2-normalised TF-IDF vector per document, in the order the documents were given.</summary>
    public IReadOnlyList<SparseVector> Documents { get; }

    /// <summary>The fitted data as ML.NET sees it (a "Features" column), which the clustering trains on.</summary>
    public IDataView Transformed { get; }

    public static TfIdfModel Fit(IReadOnlyList<string> documents, int seed)
    {
        var ml = new MLContext(seed);
        var data = ml.Data.LoadFromEnumerable(documents.Select(d => new Doc { Text = SkillNormalizer.Prepare(d) }));

        var pipeline = ml.Transforms.Text.TokenizeIntoWords("Words", nameof(Doc.Text))
            .Append(ml.Transforms.Text.RemoveDefaultStopWords("NoStop", "Words", StopWordsRemovingEstimator.Language.English))
            .Append(ml.Transforms.Text.RemoveStopWords("Kept", "NoStop", ExtraStopWords))
            .Append(ml.Transforms.Conversion.MapValueToKey("Keys", "Kept"))
            .Append(ml.Transforms.Text.ProduceNgrams("Ngrams", "Keys", ngramLength: 2, useAllLengths: true, maximumNgramsCount: MaxTerms,
                weighting: NgramExtractingEstimator.WeightingCriteria.TfIdf))
            .Append(ml.Transforms.NormalizeLpNorm("Features", "Ngrams"));

        var model = pipeline.Fit(data);
        var transformed = model.Transform(data);

        VBuffer<ReadOnlyMemory<char>> slotNames = default;
        transformed.Schema["Ngrams"].Annotations.GetValue("SlotNames", ref slotNames);
        var terms = slotNames.DenseValues().Select(n => n.ToString().Replace('|', ' ')).ToList();

        var vectors = transformed.GetColumn<VBuffer<float>>("Features").Select(ToSparse).ToList();
        return new TfIdfModel(ml, model, transformed, terms, vectors);
    }

    /// <summary>Another text in the vocabulary of the fitted documents. Words the documents never used are dropped.</summary>
    public SparseVector Transform(string text)
    {
        var data = _ml.Data.LoadFromEnumerable(new[] { new Doc { Text = SkillNormalizer.Prepare(text) } });
        var vector = _model.Transform(data).GetColumn<VBuffer<float>>("Features").FirstOrDefault();
        return ToSparse(vector);
    }

    private static SparseVector ToSparse(VBuffer<float> buffer)
    {
        var indices = new List<int>();
        var values = new List<float>();
        foreach (var item in buffer.Items(all: false))
        {
            if (item.Value == 0) continue;
            indices.Add(item.Key);
            values.Add(item.Value);
        }
        return new SparseVector(indices.ToArray(), values.ToArray());
    }
}

/// <summary>The outcome of one K-Means run over the postings.</summary>
/// <param name="Assignment">The cluster (0-based) of every document.</param>
/// <param name="Centroids">The centre (mean) of every cluster in the TF-IDF space.</param>
/// <param name="DaviesBouldin">How well separated the clusters are (lower is better).</param>
internal sealed record ClusterSolution(int K, IReadOnlyList<int> Assignment, IReadOnlyList<SparseVector> Centroids, double DaviesBouldin);

/// <summary>
/// Groups job postings of similar content with ML.NET's K-Means, choosing the number of groups by the Davies-Bouldin index. K-Means depends on
/// where it starts (on a few near-identical postings a start can even leave a group empty), so every k is run several times from different,
/// explicitly seeded starts and the best valid run is kept. A group must hold
/// at least 5% of the postings (and 3): a handful of outliers is not a family.
/// </summary>
internal static class JobClusterer
{
    /// <summary>Few postings cluster in milliseconds, so they get many restarts; with many postings each run is slower and two are enough.</summary>
    private static int Restarts(int documents) => documents < 40 ? 8 : 2;

    /// <summary>The best solution for the allowed numbers of groups, or null when the postings are too few or too alike to be grouped.</summary>
    /// <param name="maxK">The most groups to try.</param>
    public static ClusterSolution? Solve(TfIdfModel tfidf, int maxK, int seed)
    {
        var count = tfidf.Documents.Count;
        var minSize = Math.Max(3, (int)Math.Ceiling(count * 0.05));

        // With enough postings, "two families" says little: look at three and more first.
        var firstK = count >= 30 ? 3 : 2;
        ClusterSolution? best = null;

        for (var k = firstK; k <= maxK && count / k >= minSize; k++)
        {
            var solution = Fit(tfidf, k, seed, minSize);
            if (solution != null && (best == null || solution.DaviesBouldin < best.DaviesBouldin)) best = solution;
        }
        return best;
    }

    private static ClusterSolution? Fit(TfIdfModel tfidf, int k, int seed, int minSize)
    {
        ClusterSolution? best = null;
        for (var attempt = 0; attempt < Restarts(tfidf.Documents.Count); attempt++)
        {
            ClusterSolution? run;
            try
            {
                run = FitOnce(tfidf, k, seed * 7919 + k * 101 + attempt, minSize);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidCastException)
            {
                continue;   // ML.NET could not make k groups of this data (for example duplicate postings): try another start
            }

            if (run != null && (best == null || run.DaviesBouldin < best.DaviesBouldin)) best = run;
        }
        return best;
    }

    private static ClusterSolution? FitOnce(TfIdfModel tfidf, int k, int seed, int minSize)
    {
        var ml = new MLContext(seed);   // its own context: the result does not depend on what ran before
        var trainer = ml.Clustering.Trainers.KMeans(new Microsoft.ML.Trainers.KMeansTrainer.Options
        {
            FeatureColumnName = "Features",
            NumberOfClusters = k,
            MaximumNumberOfIterations = 30,
            InitializationAlgorithm = Microsoft.ML.Trainers.KMeansTrainer.InitializationAlgorithm.KMeansPlusPlus,
            NumberOfThreads = 1   // one thread: the same postings always give the same groups
        });

        var model = trainer.Fit(tfidf.Transformed);
        var assignment = model.Transform(tfidf.Transformed).GetColumn<uint>("PredictedLabel").Select(l => (int)l - 1).ToList();

        var members = Enumerable.Range(0, k).Select(c => Enumerable.Range(0, assignment.Count).Where(d => assignment[d] == c).ToList()).ToList();
        if (members.Any(m => m.Count < minSize)) return null;

        var centroids = members.Select(m => SparseVector.Mean(m.Select(d => tfidf.Documents[d]).ToList())).ToList();
        return new ClusterSolution(k, assignment, centroids, DaviesBouldin(tfidf.Documents, members, centroids));
    }

    /// <summary>
    /// The Davies-Bouldin index: for every cluster, how much it overlaps with the cluster it overlaps with most (the sum of the two clusters'
    /// average spread, divided by the distance between their centres), averaged. Lower means tighter, better separated clusters.
    /// </summary>
    internal static double DaviesBouldin(IReadOnlyList<SparseVector> documents, IReadOnlyList<List<int>> members, IReadOnlyList<SparseVector> centroids)
    {
        var spread = members.Select((m, c) => m.Average(d => SparseVector.Distance(documents[d], centroids[c]))).ToList();
        var total = 0.0;
        for (var i = 0; i < centroids.Count; i++)
        {
            var worst = 0.0;
            for (var j = 0; j < centroids.Count; j++)
            {
                if (i == j) continue;
                var apart = SparseVector.Distance(centroids[i], centroids[j]);
                worst = Math.Max(worst, apart < 1e-12 ? double.MaxValue : (spread[i] + spread[j]) / apart);
            }
            total += worst;
        }
        return total / centroids.Count;
    }
}
