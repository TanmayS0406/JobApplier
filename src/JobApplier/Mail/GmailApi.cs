using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JobApplier.Mail;

/// <summary>What the mail checker needs from Gmail. The real thing is <see cref="GmailApi"/>; tests use a fake.</summary>
public interface IGmailApi
{
    /// <summary>Message ids matching a Gmail search, newest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, int max, CancellationToken cancellationToken);

    /// <summary>Sender, subject, date, labels and snippet only: the cheap way to decide whether a message is worth reading.</summary>
    Task<MailMessage?> GetMetadataAsync(string id, CancellationToken cancellationToken);

    /// <summary>The message with its text body.</summary>
    Task<MailMessage?> GetFullAsync(string id, CancellationToken cancellationToken);
}

public sealed class GmailApiException : Exception
{
    public GmailApiException(string message, bool unauthorized = false) : base(message) => Unauthorized = unauthorized;

    /// <summary>The access token was refused: the connection has to be redone.</summary>
    public bool Unauthorized { get; }
}

/// <summary>Gmail's REST API, read-only. Nothing is ever written to the mailbox.</summary>
public sealed class GmailApi : IGmailApi
{
    private const string Base = "https://gmail.googleapis.com/gmail/v1/users/me/";
    private const int MaxBodyChars = 20_000;

    private static readonly Regex StyleOrScript = new(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"[ \t\r\f\v]+", RegexOptions.CultureInvariant);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly ITokenSource _tokens;

    static GmailApi() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // windows-1252 and friends

    public GmailApi(HttpClient http, ITokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, int max, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? page = null;
        do
        {
            var url = $"{Base}messages?q={Uri.EscapeDataString(query)}&maxResults={Math.Min(100, max - ids.Count)}" +
                      (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            var json = await GetAsync(url, cancellationToken);
            ids.AddRange(json?["messages"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).OfType<string>() ?? Enumerable.Empty<string>());
            page = json?["nextPageToken"]?.GetValue<string>();
        }
        while (page != null && ids.Count < max);
        return ids;
    }

    public async Task<MailMessage?> GetMetadataAsync(string id, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"{Base}messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date", cancellationToken);
        return json == null ? null : ParseMessage(json, includeBody: false);
    }

    public async Task<MailMessage?> GetFullAsync(string id, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"{Base}messages/{Uri.EscapeDataString(id)}?format=full", cancellationToken);
        return json == null ? null : ParseMessage(json, includeBody: true);
    }

    // ---- request ----

    private async Task<JsonNode?> GetAsync(string url, CancellationToken cancellationToken)
    {
        // No token is not proof the connection is broken: the connection marks itself when Google refuses the refresh token,
        // and a network failure while refreshing is just "try again later".
        var token = await _tokens.GetAccessTokenAsync(cancellationToken)
            ?? throw new GmailApiException("Gmail could not sign in just now (offline, or the connection has to be made again).");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;   // the message was deleted since it was listed
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new GmailApiException("Google refused the access token. Connect Gmail again.", unauthorized: true);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new GmailApiException($"Gmail answered HTTP {(int)response.StatusCode}: {ErrorMessage(body)}");
            return JsonNode.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new GmailApiException("Gmail could not be reached: " + ex.Message);
        }
    }

    private static string ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? "no details"; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return "no details"; }
    }

    // ---- parsing (static so it can be tested on sample JSON) ----

    internal static MailMessage ParseMessage(JsonNode message, bool includeBody)
    {
        var payload = message["payload"];
        string Header(string name) => payload?["headers"]?.AsArray()
            .FirstOrDefault(h => string.Equals(h?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))?["value"]?.GetValue<string>() ?? "";

        var (fromName, fromAddress) = ParseAddress(Header("From"));
        var labels = message["labelIds"]?.AsArray().Select(l => l?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();

        return new MailMessage(
            message["id"]?.GetValue<string>() ?? "",
            message["threadId"]?.GetValue<string>() ?? "",
            fromName,
            fromAddress,
            Header("Subject").Trim(),
            ParseDate(message["internalDate"]?.GetValue<string>(), Header("Date")),
            WebUtility.HtmlDecode(message["snippet"]?.GetValue<string>() ?? "").Trim(),
            labels,
            includeBody ? ExtractBody(payload) : "");
    }

    /// <summary>"Jane Doe &lt;jane@acme.com&gt;", <c>"Acme Careers" &lt;x@y.com&gt;</c> or a bare address.</summary>
    internal static (string Name, string Address) ParseAddress(string from)
    {
        var text = from.Trim();
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        if (open >= 0 && close > open)
            return (text[..open].Trim().Trim('"', '\'', ' '), text[(open + 1)..close].Trim().ToLowerInvariant());
        return ("", text.Trim().ToLowerInvariant());
    }

    private static DateTime ParseDate(string? internalDateMillis, string dateHeader)
    {
        if (long.TryParse(internalDateMillis, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return DateTimeOffset.TryParse(dateHeader, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : DateTime.UtcNow;
    }

    /// <summary>The readable text of a message: its plain-text parts, or its HTML parts with the markup removed.</summary>
    internal static string ExtractBody(JsonNode? payload)
    {
        var plain = new StringBuilder();
        var html = new StringBuilder();
        Collect(payload, plain, html);

        var text = plain.Length > 0 ? plain.ToString() : HtmlToText(html.ToString());
        text = BlankLines.Replace(Spaces.Replace(text, " "), "\n\n").Trim();
        return text.Length <= MaxBodyChars ? text : text[..MaxBodyChars];
    }

    private static void Collect(JsonNode? part, StringBuilder plain, StringBuilder html)
    {
        if (part == null) return;

        var mime = part["mimeType"]?.GetValue<string>() ?? "";
        var data = part["body"]?["data"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(data))
        {
            if (mime.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)) plain.AppendLine(Decode(data, Charset(part)));
            else if (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) html.AppendLine(Decode(data, Charset(part)));
        }
        foreach (var child in part["parts"]?.AsArray() ?? new JsonArray()) Collect(child, plain, html);
    }

    private static string? Charset(JsonNode part)
    {
        var contentType = part["headers"]?.AsArray()
            .FirstOrDefault(h => string.Equals(h?["name"]?.GetValue<string>(), "Content-Type", StringComparison.OrdinalIgnoreCase))?["value"]?.GetValue<string>();
        var match = contentType == null ? null : Regex.Match(contentType, @"charset\s*=\s*""?([\w\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    /// <summary>Gmail sends part bodies as URL-safe base64 of the raw bytes, which may be in any character set.</summary>
    internal static string Decode(string base64Url, string? charset)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        byte[] bytes;
        try { bytes = Convert.FromBase64String(s); }
        catch (FormatException) { return ""; }

        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { /* unknown charset: UTF-8 is the best guess */ }
        }
        return encoding.GetString(bytes);
    }

    internal static string HtmlToText(string html)
    {
        if (html.Length == 0) return "";
        var text = StyleOrScript.Replace(html, " ");
        text = Regex.Replace(text, @"<(br|/p|/div|/tr|/li|/h\d)\b[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Tags.Replace(text, " ");
        return WebUtility.HtmlDecode(text);
    }
}
