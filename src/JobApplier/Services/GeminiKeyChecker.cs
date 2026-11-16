using System.Text.Json;

namespace JobApplier.Services;

public enum KeyCheck
{
    /// <summary>Google accepted the key.</summary>
    Valid,

    /// <summary>Google refused the key (wrong, expired, or the API is not enabled for it).</summary>
    Invalid,

    /// <summary>Google could not be reached, so nothing is known about the key.</summary>
    Unreachable
}

public sealed record KeyCheckResult(KeyCheck Outcome, string Message);

/// <summary>
/// Asks Google whether a Gemini API key works, by listing one model. It is what turns "paste a key and hope" into an answer in a
/// second. The key goes only to Google, in the <c>x-goog-api-key</c> header (never in the address, which could end up in a log).
/// </summary>
public sealed class GeminiKeyChecker
{
    internal const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1";

    private readonly HttpClient _http;

    public GeminiKeyChecker(HttpClient http) => _http = http;

    public async Task<KeyCheckResult> CheckAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Add("x-goog-api-key", key);
            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode) return new KeyCheckResult(KeyCheck.Valid, "Google accepted the key.");

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var reason = ErrorMessage(body);

            return (int)response.StatusCode switch
            {
                // Over the quota right now, but it is a real key: that is not a reason to refuse it.
                429 => new KeyCheckResult(KeyCheck.Valid, "Google accepted the key (it is over its request quota at the moment)."),
                400 or 401 or 403 => new KeyCheckResult(KeyCheck.Invalid, "Google did not accept that key: " + reason),
                _ => new KeyCheckResult(KeyCheck.Unreachable, $"Google answered with HTTP {(int)response.StatusCode}, so the key could not be checked.")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new KeyCheckResult(KeyCheck.Unreachable, "Google could not be reached (" + ex.Message + "), so the key could not be checked.");
        }
    }

    /// <summary>The <c>error.message</c> of a Google error body, shortened; a generic phrase when there is none.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                return text.Length <= 200 ? text : text[..200] + "...";
        }
        catch (JsonException)
        {
            // not a JSON error body
        }
        return "it was rejected.";
    }
}
