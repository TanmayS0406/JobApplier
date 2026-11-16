using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobApplier.Mail;

/// <summary>What Google's token endpoint said. <see cref="Error"/> is Google's own code (<c>invalid_grant</c>, <c>invalid_client</c>, ...) or <c>unreachable</c>.</summary>
public sealed record OAuthResult(
    bool Ok, string? AccessToken = null, int ExpiresInSeconds = 0, string? RefreshToken = null, string? IdToken = null,
    string? Error = null, string? ErrorDescription = null);

/// <summary>
/// OAuth 2.0 for an installed app: authorization code with PKCE. The user signs in on Google's own page; this app only ever holds
/// the tokens it is given. The scope is read-only Gmail, so a leaked token could read mail but never send, change or delete any.
/// </summary>
public sealed class GoogleOAuthClient
{
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>Read, and nothing else: no sending, no changing, no deleting.</summary>
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/gmail.readonly";

    private readonly HttpClient _http;

    public GoogleOAuthClient(HttpClient http) => _http = http;

    // ---- PKCE (RFC 7636) ----

    /// <summary>A fresh code verifier and its S256 challenge. The verifier stays here; only the challenge goes to Google.</summary>
    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));   // 43 characters
        return (verifier, ChallengeFor(verifier));
    }

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>The value that ties the browser's return trip to the sign-in this app started; unguessable and used once.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string challenge, string? loginHint = null)
    {
        var query = new List<(string, string)>
        {
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", "openid email " + ReadOnlyScope),   // openid + email only so the connected address can be shown
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("access_type", "offline"),     // a refresh token, so no sign-in is needed for every check
            ("prompt", "consent"),          // and always one, even if this app was connected before
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) query.Add(("login_hint", loginHint));
        return AuthorizationEndpoint + "?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2)));
    }

    // ---- token endpoint ----

    public Task<OAuthResult> ExchangeCodeAsync(string clientId, string clientSecret, string code, string verifier, string redirectUri, CancellationToken cancellationToken) =>
        PostAsync(TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri
        }, cancellationToken);

    public Task<OAuthResult> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        }, cancellationToken);

    /// <summary>Tells Google to forget the grant. Best effort: disconnecting must work even when offline.</summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await _http.PostAsync(RevokeEndpoint, content, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // already gone, or no network: the local copy is deleted either way
        }
    }

    /// <summary>The e-mail address in an ID token's payload. The token came straight from Google over TLS and is used only to show who is connected.</summary>
    public static string? EmailFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = Convert.FromBase64String(PadBase64(parts[1].Replace('-', '+').Replace('_', '/')));
            return JsonNode.Parse(payload)?["email"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<OAuthResult> PostAsync(string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(endpoint, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            JsonNode? json = null;
            try { json = JsonNode.Parse(body); } catch (JsonException) { /* not JSON */ }

            if (!response.IsSuccessStatusCode)
                return new OAuthResult(false, Error: json?["error"]?.GetValue<string>() ?? $"http_{(int)response.StatusCode}",
                                       ErrorDescription: json?["error_description"]?.GetValue<string>());

            var access = json?["access_token"]?.GetValue<string>();
            if (access == null) return new OAuthResult(false, Error: "no_token", ErrorDescription: "Google's answer had no access token.");

            return new OAuthResult(true, access, json?["expires_in"]?.GetValue<int>() ?? 3600,
                                   json?["refresh_token"]?.GetValue<string>(), json?["id_token"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new OAuthResult(false, Error: "unreachable", ErrorDescription: ex.Message);
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string PadBase64(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
}
