using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobApplier.Infrastructure;

namespace JobApplier.Mail;

/// <summary>Gives out a Gmail access token. Null means Gmail is not connected (or the connection has to be redone).</summary>
public interface ITokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>What the mail checker needs to know about the connection.</summary>
public interface IMailAccess : ITokenSource
{
    bool Connected { get; }
    string Email { get; }
    void MarkNeedsReconnect();
}

/// <summary>Everything secret about the Gmail connection. Stored only in encrypted form, see <see cref="TokenVault"/>.</summary>
public sealed class GmailVault
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime? ConnectedUtc { get; set; }

    /// <summary>Google refused the refresh token (revoked, expired, or the consent screen is still in "Testing"): sign in again.</summary>
    public bool NeedsReconnect { get; set; }
}

/// <summary>
/// The vault file: JSON encrypted with Windows DPAPI for the current user, so the file is useless on another account or machine and
/// the refresh token - which is what lets this app read the mailbox - is never sitting in a readable file.
/// </summary>
public static class TokenVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JobApplier.Gmail.v1");

    public static void Save(string path, GmailVault vault)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Connecting Gmail needs Windows: the token is encrypted with Windows DPAPI.");

        var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(vault, JsonFile.Options), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The vault, or null when there is none or it cannot be read (another user, a damaged file).</summary>
    public static GmailVault? Load(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<GmailVault>(plain, JsonFile.Options);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed record GmailStatus(bool HasClient, string ClientHint, bool Connected, bool NeedsReconnect, string Email, DateTime? ConnectedUtc);

/// <summary>
/// The connection to the user's Gmail: the Google client they created, the sign-in, the refresh token and the short-lived access
/// token made from it. The sign-in is started here and finished when Google sends the browser back to <c>/Gmail/Callback</c>.
/// </summary>
public sealed class GmailConnection : IMailAccess
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    private sealed record Pending(string Verifier, string RedirectUri, DateTime CreatedUtc);

    private readonly AppPaths _paths;
    private readonly GoogleOAuthClient _oauth;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessExpiresUtc;

    public GmailConnection(AppPaths paths, GoogleOAuthClient oauth)
    {
        _paths = paths;
        _oauth = oauth;
    }

    public bool Connected => GetStatus().Connected;

    public string Email => GetStatus().Email;

    public GmailStatus GetStatus()
    {
        var vault = TokenVault.Load(_paths.GmailVaultFile);
        if (vault == null) return new GmailStatus(false, "", false, false, "", null);

        var hasClient = vault.ClientId.Length > 0 && vault.ClientSecret.Length > 0;
        var connected = hasClient && vault.RefreshToken.Length > 0 && !vault.NeedsReconnect;
        return new GmailStatus(hasClient, Hint(vault.ClientId), connected, vault.RefreshToken.Length > 0 && vault.NeedsReconnect, vault.Email, vault.ConnectedUtc);
    }

    /// <summary>Saves the client the user created in Google Cloud. A different client invalidates the old sign-in.</summary>
    public string? SaveClient(string? clientId, string? clientSecret)
    {
        clientId = (clientId ?? "").Trim();
        clientSecret = (clientSecret ?? "").Trim();

        var vault = TokenVault.Load(_paths.GmailVaultFile) ?? new GmailVault();
        if (clientId.Length == 0 || clientId.Length > 300 || clientId.Any(char.IsWhiteSpace))
            return "Paste the Client ID from Google Cloud (it ends in .apps.googleusercontent.com).";
        if (clientSecret.Length == 0 && vault.ClientId == clientId && vault.ClientSecret.Length > 0)
            clientSecret = vault.ClientSecret;   // the secret box is left empty to keep the saved one
        if (clientSecret.Length == 0 || clientSecret.Length > 300 || clientSecret.Any(char.IsWhiteSpace))
            return "Paste the Client secret from Google Cloud.";

        if (vault.ClientId != clientId)
        {
            vault.RefreshToken = "";
            vault.Email = "";
            vault.ConnectedUtc = null;
            vault.NeedsReconnect = false;
            Forget();
        }
        vault.ClientId = clientId;
        vault.ClientSecret = clientSecret;

        try
        {
            TokenVault.Save(_paths.GmailVaultFile, vault);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or CryptographicException)
        {
            return "Could not save the client: " + ex.Message;
        }
    }

    /// <summary>Starts a sign-in and returns the address of Google's consent page. <paramref name="error"/> says why not, when it returns null.</summary>
    public string? StartConnect(string redirectUri, out string? error)
    {
        error = null;
        var status = GetStatus();
        var vault = TokenVault.Load(_paths.GmailVaultFile);
        if (!status.HasClient || vault == null)
        {
            error = "Save the Client ID and secret first.";
            return null;
        }

        var (verifier, challenge) = GoogleOAuthClient.NewPkce();
        var state = GoogleOAuthClient.NewState();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in _pending.Where(p => now - p.Value.CreatedUtc > PendingLifetime).Select(p => p.Key).ToList()) _pending.Remove(stale);
            if (_pending.Count >= 5) _pending.Remove(_pending.OrderBy(p => p.Value.CreatedUtc).First().Key);
            _pending[state] = new Pending(verifier, redirectUri, now);
        }
        return GoogleOAuthClient.BuildAuthorizationUrl(vault.ClientId, redirectUri, state, challenge);
    }

    /// <summary>Finishes the sign-in when Google sends the browser back. The state must be one this app issued, and works once.</summary>
    public async Task<(bool Ok, string Message)> CompleteAsync(string? state, string? code, string? error, CancellationToken cancellationToken)
    {
        Pending? pending;
        lock (_gate)
        {
            pending = state != null && _pending.Remove(state, out var found) ? found : null;
        }
        if (pending == null || DateTime.UtcNow - pending.CreatedUtc > PendingLifetime)
            return (false, "That sign-in link is not valid any more. Press Connect Gmail again.");
        if (!string.IsNullOrEmpty(error))
            return (false, error == "access_denied" ? "Gmail was not connected: access was declined on Google's page." : "Google stopped the sign-in: " + error + ".");
        if (string.IsNullOrEmpty(code))
            return (false, "Google did not send a sign-in code. Press Connect Gmail again.");

        var vault = TokenVault.Load(_paths.GmailVaultFile);
        if (vault == null || vault.ClientId.Length == 0) return (false, "The Google client is not saved. Save the Client ID and secret, then connect.");

        var result = await _oauth.ExchangeCodeAsync(vault.ClientId, vault.ClientSecret, code, pending.Verifier, pending.RedirectUri, cancellationToken);
        if (!result.Ok) return (false, Describe(result));
        if (string.IsNullOrEmpty(result.RefreshToken))
            return (false, "Google did not give a long-lived sign-in. Remove JobApplier at https://myaccount.google.com/permissions and connect again.");

        vault.RefreshToken = result.RefreshToken;
        vault.Email = GoogleOAuthClient.EmailFromIdToken(result.IdToken) ?? vault.Email;
        vault.ConnectedUtc = DateTime.UtcNow;
        vault.NeedsReconnect = false;
        try
        {
            TokenVault.Save(_paths.GmailVaultFile, vault);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or CryptographicException)
        {
            return (false, "Connected, but the sign-in could not be saved: " + ex.Message);
        }

        lock (_gate)
        {
            _accessToken = result.AccessToken;
            _accessExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, result.ExpiresInSeconds));
        }
        return (true, "Gmail is connected" + (vault.Email.Length > 0 ? " as " + vault.Email : "") + ". Replies will be looked for automatically.");
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_accessToken != null && _accessExpiresUtc > DateTime.UtcNow.AddMinutes(1)) return _accessToken;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_accessToken != null && _accessExpiresUtc > DateTime.UtcNow.AddMinutes(1)) return _accessToken;   // someone else just did it
            }

            var vault = TokenVault.Load(_paths.GmailVaultFile);
            if (vault == null || vault.RefreshToken.Length == 0 || vault.NeedsReconnect) return null;

            var result = await _oauth.RefreshAsync(vault.ClientId, vault.ClientSecret, vault.RefreshToken, cancellationToken);
            if (!result.Ok)
            {
                // invalid_grant: revoked, expired (a client still in "Testing" expires them after 7 days), or the password changed.
                if (result.Error is "invalid_grant" or "invalid_client") MarkNeedsReconnect();
                return null;
            }

            lock (_gate)
            {
                _accessToken = result.AccessToken;
                _accessExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, result.ExpiresInSeconds));
                return _accessToken;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void MarkNeedsReconnect()
    {
        var vault = TokenVault.Load(_paths.GmailVaultFile);
        if (vault == null || vault.NeedsReconnect) return;
        vault.NeedsReconnect = true;
        TokenVault.Save(_paths.GmailVaultFile, vault);
        Forget();
    }

    /// <summary>Disconnects: Google is told to forget the grant and the stored sign-in is deleted. The client id and secret are kept.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var vault = TokenVault.Load(_paths.GmailVaultFile);
        if (vault == null) return;

        if (vault.RefreshToken.Length > 0) await _oauth.RevokeAsync(vault.RefreshToken, cancellationToken);
        vault.RefreshToken = "";
        vault.Email = "";
        vault.ConnectedUtc = null;
        vault.NeedsReconnect = false;
        TokenVault.Save(_paths.GmailVaultFile, vault);
        Forget();
    }

    /// <summary>Removes everything, including the client id and secret.</summary>
    public void RemoveEverything()
    {
        if (File.Exists(_paths.GmailVaultFile)) File.Delete(_paths.GmailVaultFile);
        Forget();
    }

    private void Forget()
    {
        lock (_gate)
        {
            _accessToken = null;
            _accessExpiresUtc = default;
        }
    }

    private static string Hint(string clientId) => clientId.Length <= 12 ? clientId : clientId[..8] + "..." + clientId[^8..];

    private static string Describe(OAuthResult result) => result.Error switch
    {
        "invalid_client" => "Google does not recognise that Client ID / secret. Check both in Google Cloud (Credentials).",
        "invalid_grant" => "Google rejected the sign-in code (it may have expired). Press Connect Gmail again.",
        "redirect_uri_mismatch" => "Google rejected the return address. Use a Desktop app client, or add this app's address as an authorised redirect URI.",
        "unreachable" => "Google could not be reached (" + result.ErrorDescription + "). Check the connection and try again.",
        _ => "Google refused the sign-in: " + (result.ErrorDescription ?? result.Error) + "."
    };
}
