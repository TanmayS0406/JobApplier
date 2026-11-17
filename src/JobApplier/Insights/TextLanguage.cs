using System.Text.RegularExpressions;

namespace JobApplier.Insights;

/// <summary>
/// Which language a text is written in, from how many of its words are the small function words of each language. Postings come in English,
/// Spanish, German and more; comparing a resume with postings in another language only measures grammar words ("und", "mit", "desarrollo"),
/// so the text analysis uses the postings in the resume's own language and says how many it left out.
/// </summary>
public static class TextLanguage
{
    private static readonly Dictionary<string, HashSet<string>> Function = new(StringComparer.Ordinal)
    {
        ["en"] = Words("the and of to in a is for with on as are you we be will our that this or an by from at have your it not can their about"),
        ["es"] = Words("de la el en y que los las del un una con para por se es al lo su como mas o sus este esta tambien entre sobre desde nuestro nuestra"),
        ["de"] = Words("und der die das mit fur von zu den im ist des dem ein eine auf bei sie wir als auch nicht sich oder werden unsere ihre"),
        ["fr"] = Words("le la les des du de et un une pour avec dans est sur par vous nous que qui au aux en ce votre notre"),
        ["pt"] = Words("de da do em e que os as dos das um uma com para por se e ao como mais ou seu sua nosso nossa"),
        ["nl"] = Words("de het een van en in is op te voor met dat die niet zijn ook aan er als bij of wij je u onze uw naar worden"),
        ["it"] = Words("il lo la le di e che un una per con in su del della dei sono come anche non nostro nostra alla delle gli")
    };

    public static string Name(string code) => code switch
    {
        "en" => "English", "es" => "Spanish", "de" => "German", "fr" => "French", "pt" => "Portuguese", "nl" => "Dutch", "it" => "Italian", _ => code
    };

    /// <summary>The language code (en, es, de, fr, pt, nl, it), or null when the text is too short or too mixed to tell.</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var words = Regex.Matches(SkillNormalizerAccents(text).ToLowerInvariant(), "[a-z]+").Select(m => m.Value).Take(600).ToList();
        if (words.Count < 20) return null;

        var scores = Function.Select(l => (Language: l.Key, Share: words.Count(l.Value.Contains) / (double)words.Count)).OrderByDescending(s => s.Share).ToList();
        return scores[0].Share >= 0.12 && scores[0].Share - scores[1].Share >= 0.04 ? scores[0].Language : null;
    }

    private static HashSet<string> Words(string list) => list.Split(' ').ToHashSet(StringComparer.Ordinal);

    private static string SkillNormalizerAccents(string text) =>
        string.Concat(text.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark));
}
