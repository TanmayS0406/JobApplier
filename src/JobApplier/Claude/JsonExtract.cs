using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobApplier.Claude;

/// <summary>
/// Finds the JSON object in a model's answer, which may be wrapped in a code fence or in a sentence. Used by the providers whose CLI
/// cannot enforce a result schema (GitHub Copilot): the format is asked for in the prompt and the object is taken from the answer.
/// </summary>
public static class JsonExtract
{
    /// <summary>The first balanced <c>{ ... }</c> in the text that parses as JSON, or null.</summary>
    public static string? FirstObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var end = MatchingBrace(text, start);
            if (end < 0) continue;

            var candidate = text.Substring(start, end - start + 1);
            try
            {
                if (JsonNode.Parse(candidate) is JsonObject) return candidate;
            }
            catch (JsonException)
            {
                // not JSON after all: keep looking
            }
        }
        return null;
    }

    private static int MatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
