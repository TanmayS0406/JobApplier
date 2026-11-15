namespace JobApplier.Infrastructure;

public static class SecretRedactor
{
    public const string Mask = "********";

    /// <summary>Replaces every occurrence of each secret in <paramref name="text"/>. Secrets shorter than 6 characters are ignored (too many false hits).</summary>
    public static string Redact(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 6) continue;
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }
        return result;
    }
}
