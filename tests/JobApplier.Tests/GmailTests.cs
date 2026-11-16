using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Mail;
using JobApplier.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

/// <summary>Stands in for Google's servers: records every request and answers with whatever the test says.</summary>
internal sealed class MailHttp : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string Body, string? Auth)> Requests { get; } = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString()));   // AbsoluteUri keeps the escaping that went on the wire
        return Respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class MailSamples
{
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Base64Url(string text, Encoding? encoding = null) => Base64Url((encoding ?? Encoding.UTF8).GetBytes(text));

    public static string IdToken(string email) => "header." + Base64Url($"{{\"email\":\"{email}\"}}") + ".signature";

    public static JsonObject Header(string name, string value) => new() { ["name"] = name, ["value"] = value };

    public static JsonObject Part(string mime, string data, string? charset = null) => new()
    {
        ["mimeType"] = mime,
        ["headers"] = new JsonArray(Header("Content-Type", mime + (charset == null ? "" : $"; charset=\"{charset}\""))),
        ["body"] = new JsonObject { ["data"] = data }
    };

    public static JsonObject Message(DateTime sent, params JsonObject[] parts) => new()
    {
        ["id"] = "m1",
        ["threadId"] = "t1",
        ["internalDate"] = new DateTimeOffset(sent, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(),
        ["snippet"] = "We&#39;re sorry to tell you",
        ["labelIds"] = new JsonArray("INBOX", "UNREAD"),
        ["payload"] = new JsonObject
        {
            ["mimeType"] = "multipart/alternative",
            ["headers"] = new JsonArray(
                Header("From", "\"Acme Careers\" <Careers@Acme.com>"),
                Header("Subject", "  Your application to Acme "),
                Header("Date", "Sat, 20 Jun 2026 12:00:00 +0000")),
            ["parts"] = new JsonArray(parts.Cast<JsonNode>().ToArray())
        }
    };
}

public class GmailApiParsingTests
{
    private static readonly DateTime Sent = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_message_becomes_sender_subject_date_labels_and_snippet()
    {
        var json = MailSamples.Message(Sent, MailSamples.Part("text/plain", MailSamples.Base64Url("Hello there")));

        var mail = GmailApi.ParseMessage(json, includeBody: true);

        Assert.Equal("m1", mail.Id);
        Assert.Equal("Acme Careers", mail.FromName);
        Assert.Equal("careers@acme.com", mail.FromAddress);
        Assert.Equal("acme.com", mail.Domain);
        Assert.Equal("Your application to Acme", mail.Subject);
        Assert.Equal(Sent, mail.DateUtc);
        Assert.Equal("We're sorry to tell you", mail.Snippet);
        Assert.Contains("UNREAD", mail.Labels);
        Assert.Equal("Hello there", mail.Body);
    }

    [Fact]
    public void The_body_is_left_out_when_only_the_header_is_wanted()
    {
        var json = MailSamples.Message(Sent, MailSamples.Part("text/plain", MailSamples.Base64Url("Hello there")));

        Assert.Equal("", GmailApi.ParseMessage(json, includeBody: false).Body);
    }

    [Fact]
    public void A_mail_with_only_html_is_turned_into_readable_text()
    {
        var html = "<html><head><style>p { color: red }</style></head><body><p>Dear Kushal,</p><p>We will <b>not</b> be moving forward.<br>Best,<br>Acme</p><script>x()</script></body></html>";
        var json = MailSamples.Message(Sent, MailSamples.Part("text/html", MailSamples.Base64Url(html)));

        var body = GmailApi.ParseMessage(json, includeBody: true).Body;

        Assert.Contains("We will not be moving forward.", body);
        Assert.Contains("Dear Kushal,", body);
        Assert.DoesNotContain("color", body);
        Assert.DoesNotContain("x()", body);
        Assert.DoesNotContain("<", body);
    }

    [Fact]
    public void Plain_text_is_preferred_over_html_and_nested_parts_are_followed()
    {
        var inner = new JsonObject
        {
            ["mimeType"] = "multipart/alternative",
            ["parts"] = new JsonArray(MailSamples.Part("text/plain", MailSamples.Base64Url("plain version")), MailSamples.Part("text/html", MailSamples.Base64Url("<p>html version</p>")))
        };
        var json = MailSamples.Message(Sent, inner);

        Assert.Equal("plain version", GmailApi.ParseMessage(json, includeBody: true).Body);
    }

    [Fact]
    public void A_body_in_another_character_set_is_decoded_with_it()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var latin = Encoding.GetEncoding("windows-1252");
        var json = MailSamples.Message(Sent, MailSamples.Part("text/plain", MailSamples.Base64Url("Reunión mañana", latin), charset: "windows-1252"));

        Assert.Equal("Reunión mañana", GmailApi.ParseMessage(json, includeBody: true).Body);
    }

    [Fact]
    public void An_unknown_character_set_falls_back_to_utf8_and_broken_data_gives_no_text()
    {
        Assert.Equal("héllo", GmailApi.Decode(MailSamples.Base64Url("héllo"), "no-such-charset"));
        Assert.Equal("", GmailApi.Decode("!!! not base64 !!!", null));
    }

    [Theory]
    [InlineData("Jane Doe <Jane@Acme.com>", "Jane Doe", "jane@acme.com")]
    [InlineData("\"Acme, Inc.\" <x@y.com>", "Acme, Inc.", "x@y.com")]
    [InlineData("plain@host.com", "", "plain@host.com")]
    [InlineData("<only@host.com>", "", "only@host.com")]
    public void An_address_is_split_into_name_and_lower_case_address(string from, string name, string address)
    {
        Assert.Equal((name, address), GmailApi.ParseAddress(from));
    }
}

public class GmailApiRequestTests
{
    private sealed class Tokens : ITokenSource
    {
        public string? Token { get; set; } = "ACCESS";
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);
    }

    private static (GmailApi Api, MailHttp Http, Tokens Tokens) Create()
    {
        var http = new MailHttp();
        var tokens = new Tokens();
        return (new GmailApi(new HttpClient(http), tokens), http, tokens);
    }

    [Fact]
    public async Task Listing_follows_the_pages_asks_read_only_and_escapes_the_search()
    {
        var (api, http, _) = Create();
        http.Respond = (request, _) => request.RequestUri!.Query.Contains("pageToken=P2")
            ? MailHttp.Json(HttpStatusCode.OK, "{\"messages\":[{\"id\":\"c\"}]}")
            : MailHttp.Json(HttpStatusCode.OK, "{\"messages\":[{\"id\":\"a\"},{\"id\":\"b\"}],\"nextPageToken\":\"P2\"}");

        var ids = await api.ListMessageIdsAsync("after:2026/06/01 -in:sent", 10, CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, ids);
        Assert.All(http.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);          // nothing is ever written to the mailbox
            Assert.Equal("Bearer ACCESS", r.Auth);
            Assert.StartsWith("https://gmail.googleapis.com/gmail/v1/users/me/messages", r.Url);
        });
        Assert.Contains("q=after%3A2026%2F06%2F01%20-in%3Asent", http.Requests[0].Url);
    }

    [Fact]
    public async Task Listing_stops_at_the_limit_even_when_there_are_more_pages()
    {
        var (api, http, _) = Create();
        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.OK, "{\"messages\":[{\"id\":\"a\"},{\"id\":\"b\"}],\"nextPageToken\":\"MORE\"}");

        var ids = await api.ListMessageIdsAsync("x", 2, CancellationToken.None);

        Assert.Equal(2, ids.Count);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_message_deleted_since_it_was_listed_is_simply_missing()
    {
        var (api, http, _) = Create();
        http.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

        Assert.Null(await api.GetMetadataAsync("gone", CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_token_is_reported_as_needing_a_new_sign_in()
    {
        var (api, http, _) = Create();
        http.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<GmailApiException>(() => api.GetFullAsync("m", CancellationToken.None));

        Assert.True(ex.Unauthorized);
    }

    [Fact]
    public async Task A_server_error_and_a_missing_token_are_not_taken_for_a_broken_connection()
    {
        var (api, http, tokens) = Create();
        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"backend error\"}}");

        var server = await Assert.ThrowsAsync<GmailApiException>(() => api.GetMetadataAsync("m", CancellationToken.None));
        Assert.False(server.Unauthorized);
        Assert.Contains("backend error", server.Message);

        tokens.Token = null;   // offline while refreshing
        var noToken = await Assert.ThrowsAsync<GmailApiException>(() => api.GetMetadataAsync("m", CancellationToken.None));
        Assert.False(noToken.Unauthorized);
    }

    [Fact]
    public async Task Metadata_requests_ask_for_headers_only()
    {
        var (api, http, _) = Create();
        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.OK, MailSamples.Message(new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)).ToJsonString());

        var mail = await api.GetMetadataAsync("m1", CancellationToken.None);

        Assert.NotNull(mail);
        Assert.Contains("format=metadata", http.Requests.Single().Url);
        Assert.DoesNotContain("format=full", http.Requests.Single().Url);
    }
}

public class GoogleOAuthTests
{
    [Fact]
    public void The_PKCE_challenge_matches_the_example_in_RFC_7636()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleOAuthClient.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Every_sign_in_gets_its_own_verifier_challenge_and_state()
    {
        var (v1, c1) = GoogleOAuthClient.NewPkce();
        var (v2, _) = GoogleOAuthClient.NewPkce();

        Assert.NotEqual(v1, v2);
        Assert.Equal(GoogleOAuthClient.ChallengeFor(v1), c1);
        Assert.InRange(v1.Length, 43, 128);
        Assert.NotEqual(GoogleOAuthClient.NewState(), GoogleOAuthClient.NewState());
    }

    [Fact]
    public void The_consent_page_asks_for_read_only_access_offline_with_PKCE_and_never_carries_a_secret()
    {
        var url = GoogleOAuthClient.BuildAuthorizationUrl("cid.apps.googleusercontent.com", "http://127.0.0.1:5189/Gmail/Callback", "STATE", "CHALLENGE");
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.StartsWith("https://accounts.google.com/", url);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("http://127.0.0.1:5189/Gmail/Callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("CHALLENGE", query["code_challenge"]);
        Assert.Equal("STATE", query["state"]);
        Assert.Equal("offline", query["access_type"]);

        var scopes = query["scope"]!.Split(' ');
        Assert.Contains("https://www.googleapis.com/auth/gmail.readonly", scopes);
        Assert.DoesNotContain(scopes, s => s.Contains("modify") || s.Contains("send") || s.Contains("compose") || s == "https://mail.google.com/");
        Assert.DoesNotContain("secret", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_connected_address_is_read_from_the_id_token_and_garbage_gives_nothing()
    {
        Assert.Equal("me@gmail.com", GoogleOAuthClient.EmailFromIdToken(MailSamples.IdToken("me@gmail.com")));
        Assert.Null(GoogleOAuthClient.EmailFromIdToken(null));
        Assert.Null(GoogleOAuthClient.EmailFromIdToken("not-a-token"));
        Assert.Null(GoogleOAuthClient.EmailFromIdToken("a.@@@.c"));
    }
}

public class GmailConnectionTests
{
    private const string Callback = "http://127.0.0.1:5189/Gmail/Callback";

    private static (GmailConnection Connection, MailHttp Http, TestEnv Env) Create()
    {
        var env = new TestEnv();
        var http = new MailHttp();
        return (new GmailConnection(env.Paths, new GoogleOAuthClient(new HttpClient(http))), http, env);
    }

    private static string StateOf(string url) => HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;

    private static void StoreConnected(TestEnv env) =>
        TokenVault.Save(env.Paths.GmailVaultFile, new GmailVault { ClientId = "c", ClientSecret = "s", RefreshToken = "RT", Email = "me@gmail.com", ConnectedUtc = DateTime.UtcNow });

    private static string TokenAnswer(string access = "AT-1") =>
        $"{{\"access_token\":\"{access}\",\"expires_in\":3600,\"refresh_token\":\"RT-1\",\"id_token\":\"{MailSamples.IdToken("me@gmail.com")}\"}}";

    [Fact]
    public async Task Connecting_trades_the_code_once_and_keeps_the_sign_in_encrypted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        Assert.Null(connection.SaveClient("client-one.apps.googleusercontent.com", "GOCSPX-very-secret-value"));

        var raw = File.ReadAllBytes(env.Paths.GmailVaultFile);
        Assert.True(raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes("GOCSPX-very-secret-value")) < 0, "the secret must not be readable in the file");
        Assert.True(raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes("client-one")) < 0, "the client id must not be readable in the file");

        var url = connection.StartConnect(Callback, out var error);
        Assert.Null(error);
        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.OK, TokenAnswer());

        var (ok, message) = await connection.CompleteAsync(StateOf(url!), "CODE-1", null, CancellationToken.None);

        Assert.True(ok, message);
        var status = connection.GetStatus();
        Assert.True(status.Connected);
        Assert.Equal("me@gmail.com", status.Email);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://oauth2.googleapis.com/token", request.Url);
        Assert.Contains("grant_type=authorization_code", request.Body);
        Assert.Contains("code=CODE-1", request.Body);
        Assert.Contains("code_verifier=", request.Body);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(Callback), request.Body);

        Assert.Equal("AT-1", await connection.GetAccessTokenAsync(CancellationToken.None));
        Assert.Single(http.Requests);   // the token just received is used, not asked for again
    }

    [Fact]
    public async Task A_sign_in_link_works_only_once_and_only_for_a_state_this_app_issued()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        connection.SaveClient("client-one", "secret-one");
        var url = connection.StartConnect(Callback, out var _ignored);

        var forged = await connection.CompleteAsync("forged-state", "CODE", null, CancellationToken.None);
        Assert.False(forged.Ok);
        Assert.Empty(http.Requests);   // nothing was sent to Google for it

        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.OK, TokenAnswer());
        Assert.True((await connection.CompleteAsync(StateOf(url!), "CODE", null, CancellationToken.None)).Ok);
        Assert.False((await connection.CompleteAsync(StateOf(url!), "CODE", null, CancellationToken.None)).Ok);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Declining_on_Googles_page_connects_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        connection.SaveClient("client-one", "secret-one");
        var url = connection.StartConnect(Callback, out var _ignored);

        var (ok, message) = await connection.CompleteAsync(StateOf(url!), null, "access_denied", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("declined", message);
        Assert.False(connection.GetStatus().Connected);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_sign_in_without_a_refresh_token_is_refused_because_it_would_not_last()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        connection.SaveClient("client-one", "secret-one");
        var url = connection.StartConnect(Callback, out var _ignored);
        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.OK, "{\"access_token\":\"AT\",\"expires_in\":3600}");

        var (ok, message) = await connection.CompleteAsync(StateOf(url!), "CODE", null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("long-lived", message);
        Assert.False(connection.GetStatus().Connected);
    }

    [Fact]
    public void Connecting_needs_a_saved_client_first()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, _, env) = Create();
        using var _e = env;

        Assert.Null(connection.StartConnect(Callback, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task The_access_token_is_reused_until_it_is_about_to_expire()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        StoreConnected(env);
        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.OK, "{\"access_token\":\"AT-2\",\"expires_in\":3600}");

        Assert.Equal("AT-2", await connection.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("AT-2", await connection.GetAccessTokenAsync(CancellationToken.None));

        var request = Assert.Single(http.Requests);
        Assert.Contains("grant_type=refresh_token", request.Body);
        Assert.Contains("refresh_token=RT", request.Body);
    }

    [Fact]
    public async Task A_revoked_or_expired_refresh_token_marks_the_connection_for_a_new_sign_in()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        StoreConnected(env);
        http.Respond = (_, _) => MailHttp.Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}");

        Assert.Null(await connection.GetAccessTokenAsync(CancellationToken.None));

        var status = connection.GetStatus();
        Assert.False(status.Connected);
        Assert.True(status.NeedsReconnect);
        Assert.True(status.HasClient);

        Assert.Null(await connection.GetAccessTokenAsync(CancellationToken.None));
        Assert.Single(http.Requests);   // Google is not asked again with a token it already refused
    }

    [Fact]
    public async Task Being_offline_is_not_taken_for_a_broken_connection()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        StoreConnected(env);
        http.Respond = (_, _) => throw new HttpRequestException("offline");

        Assert.Null(await connection.GetAccessTokenAsync(CancellationToken.None));

        var status = connection.GetStatus();
        Assert.True(status.Connected);
        Assert.False(status.NeedsReconnect);
    }

    [Fact]
    public void A_client_is_checked_before_it_is_saved_and_the_saved_secret_can_be_kept()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, _, env) = Create();
        using var _e = env;

        Assert.NotNull(connection.SaveClient("", "secret"));
        Assert.NotNull(connection.SaveClient("has space.apps.googleusercontent.com", "secret"));
        Assert.NotNull(connection.SaveClient("client-one", ""));   // a new client needs its secret
        Assert.False(connection.GetStatus().HasClient);

        Assert.Null(connection.SaveClient("client-one", "secret-one"));
        Assert.Null(connection.SaveClient("client-one", ""));      // same client, empty box: keep the secret
        Assert.Equal("secret-one", TokenVault.Load(env.Paths.GmailVaultFile)!.ClientSecret);
    }

    [Fact]
    public void A_different_client_forgets_the_old_sign_in()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, _, env) = Create();
        using var _e = env;
        StoreConnected(env);
        Assert.True(connection.GetStatus().Connected);

        Assert.Null(connection.SaveClient("another-client", "another-secret"));

        var status = connection.GetStatus();
        Assert.False(status.Connected);
        Assert.True(status.HasClient);
        Assert.Equal("", TokenVault.Load(env.Paths.GmailVaultFile)!.RefreshToken);
    }

    [Fact]
    public async Task Disconnecting_tells_Google_deletes_the_sign_in_and_keeps_the_client()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        StoreConnected(env);

        await connection.DisconnectAsync(CancellationToken.None);

        var revoke = Assert.Single(http.Requests);
        Assert.Equal("https://oauth2.googleapis.com/revoke", revoke.Url);
        Assert.Contains("token=RT", revoke.Body);
        var status = connection.GetStatus();
        Assert.False(status.Connected);
        Assert.False(status.NeedsReconnect);
        Assert.True(status.HasClient);
        Assert.Equal("", status.Email);
    }

    [Fact]
    public async Task Disconnecting_works_without_a_network()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, http, env) = Create();
        using var _ = env;
        StoreConnected(env);
        http.Respond = (_, _) => throw new HttpRequestException("offline");

        await connection.DisconnectAsync(CancellationToken.None);

        Assert.False(connection.GetStatus().Connected);
        Assert.Equal("", TokenVault.Load(env.Paths.GmailVaultFile)!.RefreshToken);
    }

    [Fact]
    public void Removing_everything_deletes_the_vault_and_a_damaged_vault_reads_as_not_connected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, _, env) = Create();
        using var _e = env;
        StoreConnected(env);
        connection.RemoveEverything();
        Assert.False(File.Exists(env.Paths.GmailVaultFile));
        Assert.False(connection.GetStatus().HasClient);

        File.WriteAllBytes(env.Paths.GmailVaultFile, new byte[] { 1, 2, 3, 4, 5 });
        Assert.False(connection.GetStatus().Connected);
        Assert.Null(TokenVault.Load(env.Paths.GmailVaultFile));
    }

    [Fact]
    public void The_status_shows_only_a_short_hint_of_the_client_id_and_nothing_secret()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (connection, _, env) = Create();
        using var _e = env;
        connection.SaveClient("1234567890-abcdefghijklmnop.apps.googleusercontent.com", "GOCSPX-hidden");

        var status = connection.GetStatus();
        var shown = JsonSerializer.Serialize(status);

        Assert.DoesNotContain("1234567890-abcdefghijklmnop", shown);
        Assert.DoesNotContain("GOCSPX", shown);
        Assert.StartsWith("12345678", status.ClientHint);
    }
}

public class ReplyStoreTests
{
    private static EmployerReply Reply(string messageId, ReplyKind kind, Guid? job = null, int daysAgo = 1) => new()
    {
        MessageId = messageId,
        ApplicationId = job ?? Guid.NewGuid(),
        JobId = job ?? Guid.NewGuid(),
        Company = "Acme",
        Subject = "s",
        Kind = kind,
        ReceivedUtc = DateTime.UtcNow.AddDays(-daysAgo)
    };

    [Fact]
    public void The_same_message_is_stored_once_and_survives_a_restart()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);

        Assert.Equal(1, store.Upsert(new[] { Reply("m1", ReplyKind.Rejected) }));
        Assert.Equal(0, store.Upsert(new[] { Reply("m1", ReplyKind.Rejected) }));

        Assert.Equal(1, new ReplyStore(env.Paths).Count);
    }

    [Fact]
    public void Storing_a_message_again_refreshes_the_verdict_but_keeps_the_row()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);
        store.Upsert(new[] { Reply("m1", ReplyKind.Unclear) });
        var id = store.All().Single().Id;

        store.Upsert(new[] { Reply("m1", ReplyKind.MovingForward) });

        var only = store.All().Single();
        Assert.Equal(id, only.Id);
        Assert.Equal(ReplyKind.MovingForward, only.Kind);
    }

    [Fact]
    public void A_decisive_mail_outranks_a_later_automatic_receipt()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);
        var job = Guid.NewGuid();
        store.Upsert(new[]
        {
            Reply("m-ack-1", ReplyKind.Acknowledged, job, daysAgo: 6),
            Reply("m-invite", ReplyKind.MovingForward, job, daysAgo: 3),
            Reply("m-ack-2", ReplyKind.Acknowledged, job, daysAgo: 1)
        });

        Assert.Equal("m-invite", store.StatusByJob()[job].MessageId);

        store.Upsert(new[] { Reply("m-no", ReplyKind.Rejected, job, daysAgo: 0) });
        Assert.Equal("m-no", store.StatusByJob()[job].MessageId);   // among decisive mails the newest wins
    }

    [Fact]
    public void A_dismissed_reply_is_gone_and_never_comes_back()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);
        store.Upsert(new[] { Reply("m1", ReplyKind.Rejected) });

        Assert.True(store.Dismiss(store.All().Single().Id));
        Assert.Equal(0, store.Upsert(new[] { Reply("m1", ReplyKind.Rejected) }));

        Assert.Equal(0, store.Count);
        Assert.True(new ReplyStore(env.Paths).IsDismissed("m1"));   // remembered across restarts
        Assert.False(store.Dismiss(Guid.NewGuid()));
    }

    [Fact]
    public void Looked_at_messages_are_remembered_and_forgotten_on_request()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);

        store.MarkSeen(new[] { "a", "b" });
        Assert.True(store.HasSeen("a"));
        Assert.True(new ReplyStore(env.Paths).HasSeen("b"));

        store.ForgetSeen();
        Assert.False(store.HasSeen("a"));
    }

    [Fact]
    public void Only_the_most_recent_messages_are_remembered()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);

        store.MarkSeen(Enumerable.Range(0, 6500).Select(i => "id" + i));

        Assert.False(store.HasSeen("id0"));
        Assert.True(store.HasSeen("id6499"));
    }

    [Fact]
    public void A_different_mailbox_starts_again_but_keeps_the_replies()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);
        store.UseAccount("one@gmail.com");
        store.MarkSeen(new[] { "a" });
        store.Upsert(new[] { Reply("m1", ReplyKind.Rejected) });

        store.UseAccount("one@gmail.com");
        Assert.True(store.HasSeen("a"));

        store.UseAccount("two@gmail.com");
        Assert.False(store.HasSeen("a"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Clearing_deletes_the_replies_and_what_was_remembered()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);
        store.Upsert(new[] { Reply("m1", ReplyKind.Rejected) });
        store.MarkSeen(new[] { "a" });
        store.RecordCheck(DateTime.UtcNow, true, null, 1);

        store.Clear();

        var again = new ReplyStore(env.Paths);
        Assert.Equal(0, again.Count);
        Assert.False(again.HasSeen("a"));
        Assert.Null(again.State().LastCheckedUtc);
    }

    [Fact]
    public void A_failed_check_keeps_the_last_success_and_says_why()
    {
        using var env = new TestEnv();
        var store = new ReplyStore(env.Paths);
        var first = DateTime.UtcNow.AddHours(-2);

        store.RecordCheck(first, true, null, 2);
        store.RecordCheck(DateTime.UtcNow, false, "Gmail answered HTTP 500", 0);

        var state = store.State();
        Assert.Equal(first, state.LastSuccessUtc);
        Assert.Equal("Gmail answered HTTP 500", state.LastError);
        Assert.Equal(2, state.LastNewReplies);
    }
}

public class MailCheckerTests
{
    private sealed class FakeAccess : IMailAccess
    {
        public bool Connected { get; set; } = true;
        public string Email { get; set; } = "me@gmail.com";
        public bool NeedsReconnect { get; private set; }
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("token");
        public void MarkNeedsReconnect() => NeedsReconnect = true;
    }

    private sealed class FakeGmail : IGmailApi
    {
        private readonly List<MailMessage> _messages = new();
        public List<string> FullFetched { get; } = new();
        public HashSet<string> Broken { get; } = new();
        public Exception? ListFails { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public string Query { get; private set; } = "";

        public void Add(string id, string from, string subject, string body, int daysAgo = 1, params string[] labels)
        {
            var (name, address) = GmailApi.ParseAddress(from);
            var snippet = body.Length <= 100 ? body : body[..100];
            _messages.Add(new MailMessage(id, "t-" + id, name, address, subject, DateTime.UtcNow.AddDays(-daysAgo), snippet, labels, body));
        }

        public void Replace(string id, string body)
        {
            var old = _messages.Single(m => m.Id == id);
            _messages[_messages.IndexOf(old)] = old with { Body = body, Snippet = body.Length <= 100 ? body : body[..100] };
        }

        public async Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, int max, CancellationToken cancellationToken)
        {
            Query = query;
            if (Gate != null) await Gate.Task;
            if (ListFails != null) throw ListFails;
            return _messages.Select(m => m.Id).ToList();
        }

        public Task<MailMessage?> GetMetadataAsync(string id, CancellationToken cancellationToken)
        {
            if (Broken.Contains(id)) throw new GmailApiException("Gmail answered HTTP 500: backend error");
            return Task.FromResult<MailMessage?>(_messages.SingleOrDefault(m => m.Id == id) is { } m ? m with { Body = "" } : null);
        }

        public Task<MailMessage?> GetFullAsync(string id, CancellationToken cancellationToken)
        {
            FullFetched.Add(id);
            return Task.FromResult(_messages.SingleOrDefault(m => m.Id == id));
        }
    }

    private sealed class Rig : IDisposable
    {
        public TestEnv Env { get; } = new();
        public FakeAccess Access { get; } = new();
        public FakeGmail Gmail { get; } = new();
        public AccountService Accounts { get; }
        public ReplyStore Store { get; }
        public MailChecker Checker { get; }
        public ApplicationRecord? Applied { get; }

        public Rig(bool applied = true)
        {
            var settings = new SettingsService(Env.Paths);
            settings.Save(new AppSettings { AccountsDocumentPath = Path.Combine(Env.Root, "a.docx") });
            Accounts = new AccountService(Env.Paths, settings, NullLogger<AccountService>.Instance);
            Store = new ReplyStore(Env.Paths);
            Checker = new MailChecker(Access, Gmail, Accounts, new ProfileService(Env.Paths), Store, NullLogger<MailChecker>.Instance);
            if (applied) Applied = Apply("Acme Corp", "Software Engineer", "https://acme.com/jobs/1");
        }

        public ApplicationRecord Apply(string company, string role, string url) => Accounts.RecordApplication(new ApplicationRecord
        {
            JobId = Guid.NewGuid(), Company = company, Role = role, Url = url, AppliedUtc = DateTime.UtcNow.AddDays(-3)
        });

        public void Dispose() => Env.Dispose();
    }

    private const string Rejection = "Unfortunately, we have decided not to move forward with your application.";

    [Fact]
    public async Task A_rejection_from_the_company_is_found_classified_and_attached_to_its_job()
    {
        using var rig = new Rig();
        rig.Gmail.Add("m1", "Acme Careers <careers@acme.com>", "Update on your application", Rejection);

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.NewReplies);
        var reply = rig.Store.All().Single();
        Assert.Equal(ReplyKind.Rejected, reply.Kind);
        Assert.Equal("Acme Corp", reply.Company);
        Assert.Equal(rig.Applied!.JobId, reply.JobId);
        Assert.Equal(rig.Applied.Id, reply.ApplicationId);
        Assert.Same(reply, rig.Store.StatusByJob()[rig.Applied.JobId]);   // the results table finds it by job
        Assert.Contains("not to move forward", reply.Reason);
    }

    [Fact]
    public async Task The_search_skips_sent_mail_and_promotions_and_starts_at_the_first_application()
    {
        using var rig = new Rig();

        await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.Contains("-in:sent", rig.Gmail.Query);
        Assert.Contains("-category:promotions", rig.Gmail.Query);
        Assert.Contains("after:" + rig.Applied!.AppliedUtc.AddDays(-1).ToString("yyyy/MM/dd"), rig.Gmail.Query);
    }

    [Fact]
    public async Task Mail_that_has_nothing_to_do_with_applications_is_never_read_in_full()
    {
        using var rig = new Rig();
        rig.Gmail.Add("sale", "Shop <deals@shop.example>", "Summer sale: 50% off", "Everything must go.");
        rig.Gmail.Add("bill", "Power Co <billing@power.example>", "Your invoice", "Amount due: 40 EUR");

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, result.NewReplies);
        Assert.Empty(rig.Gmail.FullFetched);
    }

    [Fact]
    public async Task A_message_is_read_once_and_stored_once()
    {
        using var rig = new Rig();
        rig.Gmail.Add("m1", "Acme Careers <careers@acme.com>", "Update on your application", Rejection);

        await rig.Checker.CheckAsync(false, CancellationToken.None);
        var again = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.Equal(0, again.Examined);
        Assert.Equal(0, again.NewReplies);
        Assert.Equal(1, rig.Store.Count);
        Assert.Single(rig.Gmail.FullFetched);
    }

    [Fact]
    public async Task A_rescan_reads_everything_again_and_corrects_the_verdict()
    {
        using var rig = new Rig();
        rig.Gmail.Add("m1", "Acme Careers <careers@acme.com>", "Update on your application", Rejection);
        await rig.Checker.CheckAsync(false, CancellationToken.None);
        var rowId = rig.Store.All().Single().Id;

        rig.Gmail.Replace("m1", "We would like to invite you to a video interview next week.");
        var result = await rig.Checker.CheckAsync(true, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        var reply = rig.Store.All().Single();
        Assert.Equal(rowId, reply.Id);
        Assert.Equal(ReplyKind.MovingForward, reply.Kind);
    }

    [Fact]
    public async Task A_reply_the_user_dismissed_is_not_matched_again_even_by_a_rescan()
    {
        using var rig = new Rig();
        rig.Gmail.Add("m1", "Acme Careers <careers@acme.com>", "Update on your application", Rejection);
        await rig.Checker.CheckAsync(false, CancellationToken.None);
        rig.Store.Dismiss(rig.Store.All().Single().Id);

        await rig.Checker.CheckAsync(true, CancellationToken.None);

        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public async Task A_job_alert_that_only_mentions_the_company_in_its_text_is_not_a_reply()
    {
        using var rig = new Rig();
        rig.Apply("Globex Industries", "Software Engineer", "https://globex.example/careers/1");
        rig.Gmail.Add("alert", "Job Board <alerts@jobboard.example>", "New jobs for you", "Globex Industries is hiring a Software Engineer. Apply now.");

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Contains("alert", rig.Gmail.FullFetched);   // it was worth reading...
        Assert.Equal(0, rig.Store.Count);                   // ...but it is not an answer
    }

    [Fact]
    public async Task Mail_the_user_sent_or_wrote_to_themselves_is_ignored()
    {
        using var rig = new Rig();
        rig.Gmail.Add("mine", "Me <me@gmail.com>", "Application to Acme", "Draft of my follow-up to Acme about my application.");
        rig.Gmail.Add("sent", "Acme Careers <careers@acme.com>", "Your application", Rejection, 1, "SENT");

        await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public async Task Nothing_is_looked_for_before_an_application_has_been_submitted()
    {
        using var rig = new Rig(applied: false);
        rig.Gmail.Add("m1", "Acme Careers <careers@acme.com>", "Update on your application", Rejection);

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("No applications", result.Message);
        Assert.Empty(rig.Gmail.Query);   // Gmail was not even asked
    }

    [Fact]
    public async Task Without_a_connection_nothing_happens()
    {
        using var rig = new Rig();
        rig.Access.Connected = false;

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(rig.Gmail.Query);
    }

    [Fact]
    public async Task A_refused_sign_in_marks_the_connection_and_says_so()
    {
        using var rig = new Rig();
        rig.Gmail.ListFails = new GmailApiException("Google refused the access token.", unauthorized: true);

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(rig.Access.NeedsReconnect);
        Assert.Contains("Connect Gmail again", result.Message);
        Assert.Contains("Connect Gmail again", rig.Store.State().LastError);
    }

    [Fact]
    public async Task A_server_hiccup_is_reported_without_breaking_the_connection()
    {
        using var rig = new Rig();
        rig.Gmail.ListFails = new GmailApiException("Gmail answered HTTP 503: unavailable");

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(rig.Access.NeedsReconnect);
        Assert.Contains("503", rig.Store.State().LastError);
        Assert.Null(rig.Store.State().LastSuccessUtc);
    }

    [Fact]
    public async Task One_message_that_will_not_load_does_not_hold_up_the_rest_and_is_tried_again_later()
    {
        using var rig = new Rig();
        rig.Gmail.Add("bad", "Acme Careers <careers@acme.com>", "Your application", Rejection);
        rig.Gmail.Add("good", "Acme Careers <careers@acme.com>", "Update on your application", "We would like to invite you to a video interview.");
        rig.Gmail.Broken.Add("bad");

        var result = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, result.NewReplies);
        Assert.False(rig.Store.HasSeen("bad"));   // not given up on
        Assert.True(rig.Store.HasSeen("good"));

        rig.Gmail.Broken.Clear();
        var later = await rig.Checker.CheckAsync(false, CancellationToken.None);
        Assert.Equal(1, later.NewReplies);
        Assert.Equal(2, rig.Store.Count);
    }

    [Fact]
    public async Task A_second_check_at_the_same_time_is_turned_away()
    {
        using var rig = new Rig();
        rig.Gmail.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = rig.Checker.CheckAsync(false, CancellationToken.None);
        Assert.True(rig.Checker.IsRunning);
        var second = await rig.Checker.CheckAsync(false, CancellationToken.None);

        Assert.False(second.Ok);
        Assert.Contains("already running", second.Message);

        rig.Gmail.Gate.SetResult();
        Assert.True((await first).Ok);
        Assert.False(rig.Checker.IsRunning);
    }

    [Fact]
    public async Task Later_checks_only_list_the_last_few_days()
    {
        using var rig = new Rig();
        await rig.Checker.CheckAsync(false, CancellationToken.None);

        await rig.Checker.CheckAsync(false, CancellationToken.None);

        // the first application was 3 days ago; after a success the window is the last 2 days, not "since the application"
        Assert.Contains("after:" + DateTime.UtcNow.AddDays(-2).ToString("yyyy/MM/dd"), rig.Gmail.Query);
    }
}

public class GmailControllerTests
{
    private sealed class StubUrl : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => "/" + actionContext.Controller + "/" + actionContext.Action;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
        public string? Link(string? routeName, object? values) => null;
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class NoGmail : IGmailApi
    {
        public Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, int max, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<MailMessage?> GetMetadataAsync(string id, CancellationToken cancellationToken) => Task.FromResult<MailMessage?>(null);
        public Task<MailMessage?> GetFullAsync(string id, CancellationToken cancellationToken) => Task.FromResult<MailMessage?>(null);
    }

    private static (GmailController Controller, GmailConnection Connection, ReplyStore Store, TestEnv Env) Create(string host = "127.0.0.1:5189")
    {
        var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        var accounts = new AccountService(env.Paths, settings, NullLogger<AccountService>.Instance);
        var connection = new GmailConnection(env.Paths, new GoogleOAuthClient(new HttpClient(new MailHttp())));
        var store = new ReplyStore(env.Paths);
        var checker = new MailChecker(connection, new NoGmail(), accounts, new ProfileService(env.Paths), store, NullLogger<MailChecker>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString(host);
        var controller = new GmailController(connection, checker, store, accounts, new StubLifetime(), NullLogger<GmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempData()),
            Url = new StubUrl()
        };
        return (controller, connection, store, env);
    }

    private static JsonElement Body(IActionResult result)
    {
        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Connecting_returns_Googles_page_with_this_apps_own_return_address()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (controller, connection, _, env) = Create();
        using var _e = env;
        connection.SaveClient("client-one.apps.googleusercontent.com", "secret-one");

        var answer = Body(controller.Connect());

        Assert.True(answer.GetProperty("ok").GetBoolean());
        var url = answer.GetProperty("url").GetString()!;
        Assert.StartsWith("https://accounts.google.com/", url);
        Assert.Equal("http://127.0.0.1:5189/Gmail/Callback", HttpUtility.ParseQueryString(new Uri(url).Query)["redirect_uri"]);
    }

    [Fact]
    public void Connecting_is_refused_when_the_app_is_opened_under_another_address()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (controller, connection, _, env) = Create(host: "my-pc.example.com");
        using var _e = env;
        connection.SaveClient("client-one", "secret-one");

        var answer = Body(controller.Connect());

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("127.0.0.1", answer.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_return_from_Google_with_an_unknown_state_connects_nothing_and_says_so()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (controller, connection, _, env) = Create();
        using var _e = env;
        connection.SaveClient("client-one", "secret-one");

        var result = await controller.Callback("not-issued-by-us", "CODE", null, CancellationToken.None);

        Assert.Equal("Index", Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Contains("not valid", (string)controller.TempData["Error"]!);
        Assert.False(connection.GetStatus().Connected);
    }

    [Fact]
    public void The_status_never_carries_the_secret_or_a_token()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (controller, connection, _, env) = Create();
        using var _e = env;
        connection.SaveClient("1234567890-abcdefghijklmnop.apps.googleusercontent.com", "GOCSPX-hidden-secret");

        var json = Body(controller.Status()).GetRawText();

        Assert.DoesNotContain("GOCSPX", json);
        Assert.DoesNotContain("1234567890-abcdefghijklmnop", json);
        Assert.Contains("\"hasClient\":true", json);
        Assert.Contains("\"connected\":false", json);
    }

    [Fact]
    public void Saving_a_client_does_not_echo_it_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (controller, _, _, env) = Create();
        using var _e = env;

        var json = Body(controller.SaveClient("client-one.apps.googleusercontent.com", "GOCSPX-hidden-secret")).GetRawText();

        Assert.DoesNotContain("GOCSPX", json);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("/Views/Shared/_Replies.cshtml")]   // rendered by GmailController.Replies (which only searches Views/Gmail and Views/Shared) and by the dashboard
    [InlineData("/Views/Home/Index.cshtml")]
    [InlineData("/Views/Home/_Live.cshtml")]
    [InlineData("/Views/Settings/Index.cshtml")]
    public void The_views_behind_the_Gmail_pages_are_compiled_where_the_controllers_look_for_them(string identifier)
    {
        var compiled = typeof(GmailController).Assembly
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemAttribute>()
            .Select(a => a.Identifier);

        Assert.Contains(identifier, compiled);
    }

    [Fact]
    public void A_reply_can_be_dismissed_and_clearing_deletes_them_all()
    {
        var (controller, _, store, env) = Create();
        using var _e = env;
        store.Upsert(new[]
        {
            new EmployerReply { MessageId = "m1", Kind = ReplyKind.Rejected },
            new EmployerReply { MessageId = "m2", Kind = ReplyKind.Acknowledged }
        });

        Assert.True(Body(controller.Dismiss(store.All().First().Id)).GetProperty("ok").GetBoolean());
        Assert.Equal(1, store.Count);
        Assert.False(Body(controller.Dismiss(Guid.NewGuid())).GetProperty("ok").GetBoolean());

        controller.ClearReplies();
        Assert.Equal(0, store.Count);
    }
}
