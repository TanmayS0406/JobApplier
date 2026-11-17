using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JobApplier.Insights;

/// <summary>
/// Turns the many ways a posting words one skill into one key, so that "Kubernetes", "K8s" and "Experience with Kubernetes (K8s)"
/// count as the same gap. Filler ("experience with", "strong", "years of") is dropped, well-known abbreviations are
/// expanded, plurals are folded, and the words are put in a fixed order. Whatever still differs only by spelling is merged by
/// <see cref="SkillIndex"/>.
/// </summary>
public static class SkillNormalizer
{
    // Hyphenated spellings that must be recognised before hyphens are turned into spaces ("machine-learning" is "machine learning").
    private static readonly (string From, string To)[] HyphenSpecials = { ("ci-cd", "cicd"), ("t-sql", "tsql"), ("object-oriented", "oop") };

    // Spellings with punctuation that would be lost if punctuation were simply removed ("C#" must not become "c").
    private static readonly (string From, string To)[] PunctuationSpecials =
    {
        ("c#", "csharp"), ("f#", "fsharp"), ("c++", "cpp"), ("asp.net core", "aspnet"), ("asp.net", "aspnet"), (".net core", "dotnet"),
        (".net framework", "dotnet"), (".net", "dotnet"), ("node.js", "nodejs"), ("vue.js", "vuejs"), ("react.js", "react"), ("next.js", "nextjs"),
        ("express.js", "expressjs"), ("ci/cd", "cicd"), ("ci cd", "cicd"), ("a/b", "ab"), ("pl/sql", "plsql"), ("ms sql", "mssql")
    };

    // Phrases that name one thing. For skill keys they are written as one word; in the text model they stay phrases (the pair "machine learning").
    private static readonly (string From, string To)[] PhraseSpecials =
    {
        ("sql server", "mssql"), ("amazon web services", "aws"), ("google cloud platform", "gcp"), ("google cloud", "gcp"),
        ("natural language processing", "nlp"), ("artificial intelligence", "ai"), ("machine learning", "ml"), ("object oriented", "oop")
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["k8s"] = "kubernetes", ["js"] = "javascript", ["ts"] = "typescript", ["py"] = "python", ["postgres"] = "postgresql",
        ["psql"] = "postgresql", ["mongo"] = "mongodb", ["golang"] = "go", ["reactjs"] = "react", ["vuejs"] = "vue", ["nodejs"] = "node",
        ["angularjs"] = "angular", ["restful"] = "rest", ["aspnet"] = "dotnet", ["dotnetcore"] = "dotnet", ["sklearn"] = "scikit"
    };

    /// <summary>Words that say how well or for how long a skill is known, not which skill it is.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "experience", "experienced", "knowledge", "knowledgeable", "proficiency", "proficient", "familiarity", "familiar", "understanding",
        "strong", "solid", "good", "excellent", "great", "hands", "on", "working", "work", "ability", "able", "years", "year", "minimum",
        "preferred", "plus", "required", "requirement", "requirements", "basic", "advanced", "demonstrated", "proven", "track", "record",
        "expertise", "skills", "skill", "background", "using", "use", "with", "in", "of", "the", "a", "an", "and", "or", "to", "for", "at",
        "as", "by", "from", "including", "such", "etc", "e", "g", "ie", "eg", "within", "across", "related", "relevant", "equivalent",
        "strongly", "highly", "extensive", "professional", "practical", "commercial", "previous", "prior", "good", "nice", "have", "is", "are",
        "de", "en", "con", "y", "o", "el", "la", "los", "las", "un", "una", "experiencia", "conocimientos", "conocimiento", "sólidos"
    };

    /// <summary>"Python programming", "SQL databases": the last word names a kind of thing, the word before it is the skill.</summary>
    private static readonly HashSet<string> TrailingGeneric = new(StringComparer.Ordinal)
    {
        "programming", "language", "languages", "framework", "frameworks", "technologies", "technology", "tools", "tool", "platform",
        "platforms", "methodologies", "methodology", "concepts", "principles", "databases", "database", "libraries", "library",
        "pipelines", "pipeline"
    };

    private static readonly HashSet<string> SoftKeys = new[]
    {
        "communication", "teamwork", "collaboration", "problem solving", "time management", "adaptability", "attention to detail",
        "interpersonal", "self motivated", "motivated", "proactive", "creativity", "critical thinking", "work ethic", "team player",
        "organizational", "leadership", "analytical", "multitasking", "initiative", "autonomy", "autonomous"
    }.Select(Key).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A text as plain words for the text model: lower case, no accents, "C#" / ".NET" / "CI/CD" kept as words, abbreviations spelled out
    /// ("k8s" is "kubernetes"), punctuation and numbers gone. The same spelling rules the skill keys use, so the two agree.
    /// </summary>
    public static string Prepare(string? text) => string.Join(' ', Words(text, phrases: false).Where(w => !w.All(char.IsDigit)));

    private static List<string> Words(string? text, bool phrases)
    {
        var lower = StripAccents(text ?? "").ToLowerInvariant();
        foreach (var (from, to) in HyphenSpecials) lower = lower.Replace(from, " " + to + " ");
        lower = lower.Replace('-', ' ').Replace('_', ' ');
        foreach (var (from, to) in PunctuationSpecials) lower = lower.Replace(from, " " + to + " ");
        if (phrases)
            foreach (var (from, to) in PhraseSpecials) lower = lower.Replace(from, " " + to + " ");

        var expanded = new List<string>();
        foreach (var word in Regex.Split(lower, @"[^a-z0-9]+").Where(w => w.Length > 0))
            expanded.AddRange(Aliases.TryGetValue(word, out var alias) ? alias.Split(' ') : new[] { word });
        return expanded;
    }

    /// <summary>The distinct stems of the words of <paramref name="text"/> after the filler is gone, in order of appearance.</summary>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        var kept = Words(text, phrases: true).Where(w => !Filler.Contains(w) && !w.All(char.IsDigit)).ToList();
        if (kept.Count > 1 && TrailingGeneric.Contains(kept[^1])) kept.RemoveAt(kept.Count - 1);

        var stems = new List<string>();
        foreach (var token in kept.Select(Stem))
            if (token.Length > 0 && !stems.Contains(token)) stems.Add(token);
        return stems;
    }

    /// <summary>The same skill always gives the same key: its stems, sorted. Empty when the text was only filler.</summary>
    public static string Key(string? text) => string.Join(' ', Tokens(text).OrderBy(t => t, StringComparer.Ordinal));

    public static bool IsSoft(string key) => SoftKeys.Contains(key);

    /// <summary>Requirements about the person, not their skills: work authorisation, visa, relocation, location, clearance. A resume can state them if true, but cannot gain them.</summary>
    private static readonly HashSet<string> EligibilityWords = new(StringComparer.Ordinal)
    {
        "authorisation", "authorization", "visa", "sponsorship", "citizenship", "residency", "relocation", "relocate", "clearance", "permit",
        "eligible", "eligibility", "nationality", "right", "location", "onsite", "hybrid", "remote", "commute"
    };

    public static bool IsEligibility(string key) => key.Split(' ').Any(EligibilityWords.Contains);

    // "CI/CD" and "TCP/IP" are one skill; every other "/" separates two.
    private static readonly string[] ProtectedSlashes = { "ci/cd", "tcp/ip", "pl/sql", "a/b", "i/o", "ui/ux", "b2b/b2c" };

    private static readonly Regex Remarks = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>"R&amp;D" is a department, not the language R followed by a stray D.</summary>
    private static readonly Regex ResearchAndDevelopment = new(@"\bR\s*&\s*D\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A "+" separates two skills ("HTML + CSS", "Python+Django") but is part of "C++".
    private static readonly Regex AtomSeparators = new(@"\s*(?:,|;|/|\||&|(?<=[A-Za-z0-9\s])\+(?=\s*[A-Za-z])|\b(?:and|or|y|o|u|und|oder)\b)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Words that make a long entry a sentence ("Job description was empty, so requirements could not be assessed") rather than a list of skills.
    private static readonly Regex SentenceWords = new(@"\b(?:was|were|is|are|be|been|could|would|should|will|cannot|not|because|which|that)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The separate skills inside one entry of a skills list. Claude writes "Docker and Kubernetes", "DDD, CQRS", "RabbitMQ, Azure Service Bus o Kafka"
    /// and "Docker / Kubernetes (not shown)"; each of those is two or three skills that other jobs list on their own. Remarks in brackets are
    /// dropped, and an entry that is really a sentence ("Job description was empty, so specific requirements could not be assessed") is not a skill.
    /// </summary>
    public static IReadOnlyList<string> Atoms(string? raw)
    {
        var text = ResearchAndDevelopment.Replace(Remarks.Replace(raw ?? "", " "), " ");
        if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6 && SentenceWords.IsMatch(text)) return Array.Empty<string>();

        foreach (var protectedSlash in ProtectedSlashes)
            text = Regex.Replace(text, Regex.Escape(protectedSlash), m => m.Value.Replace('/', '\u0001'), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var atoms = new List<string>();
        foreach (var part in AtomSeparators.Split(text))
        {
            var atom = Display(part.Replace('\u0001', '/'));
            var key = Key(atom);
            if (key.Length == 0) continue;
            if (key.Length == 1 && key is not ("c" or "r")) continue;   // a stray letter, but "C" and "R" are languages
            if (Tokens(atom).Count > 6) continue;                          // a sentence, not a skill
            atoms.Add(atom);
        }
        return atoms;
    }

    /// <summary>The text as shown to the user: one line, trimmed, no trailing punctuation, not longer than 60 characters.</summary>
    public static string Display(string? text)
    {
        var line = Regex.Replace((text ?? "").Trim(), @"\s+", " ").TrimEnd('.', ',', ';', ':', ' ');
        return line.Length <= 60 ? line : line[..60].TrimEnd() + "...";
    }

    /// <summary>Words that really end in "s" in the singular, so that stripping the "s" would be wrong ("apis" is "api", "redis" is not "redi").</summary>
    private static readonly HashSet<string> EndsInS = new(StringComparer.Ordinal) { "redis", "analysis", "basis", "axis", "chassis", "tennis", "lens", "canvas" };

    private static string Stem(string token)
    {
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal)) return token[..^3] + "y";
        if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal)
            && !token.EndsWith("us", StringComparison.Ordinal) && !EndsInS.Contains(token))
            return token[..^1];
        return token;
    }

    private static string StripAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>
/// Every skill wording found in the jobs, grouped: wordings with the same <see cref="SkillNormalizer.Key"/> are one skill, and keys that
/// differ only by a typo or a spelling variant ("organisation" / "organization") are merged when their character trigrams are nearly
/// identical. A group is shown under its most common wording.
/// </summary>
public sealed class SkillIndex
{
    /// <summary>How alike two keys must be (cosine of their character trigrams) to be treated as one skill.</summary>
    internal const double MergeSimilarity = 0.8;

    private readonly Dictionary<string, string> _keyOfRaw = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Variant, int Count)>> _variants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);

    private SkillIndex() { }

    public IReadOnlyCollection<string> Keys => _labels.Keys;

    public static SkillIndex Build(IEnumerable<string> rawSkills)
    {
        var index = new SkillIndex();

        // 1. wordings with the same key
        var byKey = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var display in rawSkills.SelectMany(SkillNormalizer.Atoms))
        {
            var key = SkillNormalizer.Key(display);
            if (key.Length == 0) continue;

            if (!byKey.TryGetValue(key, out var forms)) byKey[key] = forms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            forms[display] = forms.GetValueOrDefault(display) + 1;
        }

        // 2. keys that differ only by spelling
        var keys = byKey.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var parent = keys.ToDictionary(k => k, k => k, StringComparer.Ordinal);
        string Find(string k) { while (parent[k] != k) k = parent[k] = parent[parent[k]]; return k; }

        var grams = keys.ToDictionary(k => k, Trigrams, StringComparer.Ordinal);

        // Only keys that share character trigrams can be alike, so keys are looked up through an inverted index instead of comparing every pair.
        var keysWithGram = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
            foreach (var gram in grams[keys[i]].Keys)
            {
                if (!keysWithGram.TryGetValue(gram, out var list)) keysWithGram[gram] = list = new List<int>();
                list.Add(i);
            }

        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i].Length < 6) continue;   // short keys (sql, go, r) differ by a letter and mean different things

            var shared = new Dictionary<int, int>();
            foreach (var gram in grams[keys[i]].Keys)
                foreach (var j in keysWithGram[gram])
                    if (j > i) shared[j] = shared.GetValueOrDefault(j) + 1;

            foreach (var j in shared.Keys)
            {
                if (keys[j].Length < 6 || Math.Abs(keys[i].Length - keys[j].Length) > 3) continue;
                if (Cosine(grams[keys[i]], grams[keys[j]]) < MergeSimilarity) continue;

                var (a, b) = (Find(keys[i]), Find(keys[j]));
                if (a == b) continue;
                // the key with more occurrences leads
                var countA = byKey[a].Values.Sum(); var countB = byKey[b].Values.Sum();
                if (countB > countA || (countB == countA && string.CompareOrdinal(b, a) < 0)) parent[a] = b; else parent[b] = a;
            }
        }

        // 3. one entry per group
        foreach (var group in keys.GroupBy(Find))
        {
            var forms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in group)
            {
                index._keyOfRaw[member] = group.Key;
                foreach (var (form, count) in byKey[member]) forms[form] = forms.GetValueOrDefault(form) + count;
            }

            // the most common wording; among equally common ones the most informative that is still short ("Kubernetes (K8s)" over "K8s")
            var ordered = forms.OrderByDescending(f => f.Value).ThenByDescending(f => f.Key.Length <= 24 ? f.Key.Length : 0)
                .ThenBy(f => f.Key.Length).ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
            index._variants[group.Key] = ordered.Select(f => (f.Key, f.Value)).ToList();
            index._labels[group.Key] = ordered[0].Key;
        }
        return index;
    }

    /// <summary>The skills an entry of a skills list contains ("Docker and Kubernetes" is two), as keys. Empty when the entry is nothing but filler.</summary>
    public IReadOnlyList<string> KeysOf(string? raw) =>
        SkillNormalizer.Atoms(raw)
            .Select(atom => SkillNormalizer.Key(atom))
            .Where(key => key.Length > 0 && _keyOfRaw.ContainsKey(key))
            .Select(key => _keyOfRaw[key])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The key of the (first) skill an entry contains, or null when it is nothing but filler.</summary>
    public string? KeyOf(string? raw) => KeysOf(raw).FirstOrDefault();

    public string LabelOf(string key) => _labels.GetValueOrDefault(key, key);

    public IReadOnlyList<string> VariantsOf(string key) =>
        _variants.TryGetValue(key, out var variants) ? variants.Select(v => v.Variant).ToList() : new List<string>();

    private static Dictionary<string, int> Trigrams(string text)
    {
        var padded = "  " + text + " ";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= padded.Length; i++)
        {
            var gram = padded.Substring(i, 3);
            counts[gram] = counts.GetValueOrDefault(gram) + 1;
        }
        return counts;
    }

    private static double Cosine(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        double dot = 0;
        foreach (var (gram, count) in a)
            if (b.TryGetValue(gram, out var other)) dot += (double)count * other;
        var norm = Math.Sqrt(a.Values.Sum(v => (double)v * v)) * Math.Sqrt(b.Values.Sum(v => (double)v * v));
        return norm == 0 ? 0 : dot / norm;
    }
}
