using System.Net;
using System.Text.Json;
using JobApplier.Claude;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Tests;

public class GeminiEnvFileTests
{
    private const string Key = "AIzaFAKE_exampleKey_0123456789ghij";

    [Fact]
    public void A_saved_key_is_written_where_the_CLI_reads_it_and_read_back()
    {
        using var env = new TestEnv();

        GeminiCli.StoreApiKey(Key, env.Root);

        Assert.Equal(Path.Combine(env.Root, ".gemini", ".env"), GeminiCli.EnvFile(env.Root));
        Assert.Equal("GEMINI_API_KEY=" + Key, File.ReadAllText(GeminiCli.EnvFile(env.Root)).Trim());
        Assert.Equal(("GEMINI_API_KEY", Key), GeminiCli.ReadStoredApiKey(env.Root));
    }

    [Fact]
    public void Saving_keeps_every_other_line_and_replaces_an_earlier_key()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".gemini"));
        File.WriteAllLines(GeminiCli.EnvFile(env.Root), new[]
        {
            "# my settings", "FOO=bar", "export GEMINI_API_KEY=\"old-key-0000000000000\"", "", "GEMINI_API_KEY=duplicate-000000000000", "BAZ=qux"
        });

        GeminiCli.StoreApiKey(Key, env.Root);

        var lines = File.ReadAllLines(GeminiCli.EnvFile(env.Root));
        Assert.Equal(new[] { "# my settings", "FOO=bar", "GEMINI_API_KEY=" + Key, "", "BAZ=qux" }, lines);
    }

    [Theory]
    [InlineData("GEMINI_API_KEY=abc12345678901234567890")]
    [InlineData("export GEMINI_API_KEY=abc12345678901234567890")]
    [InlineData("GEMINI_API_KEY=\"abc12345678901234567890\"")]
    [InlineData("  GEMINI_API_KEY = 'abc12345678901234567890'  ")]
    public void A_key_line_is_understood_in_the_usual_dotenv_spellings(string line)
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".gemini"));
        File.WriteAllLines(GeminiCli.EnvFile(env.Root), new[] { "# comment", "OTHER=1", line });

        Assert.Equal("abc12345678901234567890", GeminiCli.ReadStoredApiKey(env.Root)!.Value.Value);
    }

    [Fact]
    public void Comments_blank_values_and_other_variables_are_not_keys()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".gemini"));
        File.WriteAllLines(GeminiCli.EnvFile(env.Root), new[] { "# GEMINI_API_KEY=commented-out-0000000", "GEMINI_API_KEY=", "FOO=bar" });

        Assert.Null(GeminiCli.ReadStoredApiKey(env.Root));
        Assert.Null(GeminiCli.ReadStoredApiKey(Path.Combine(env.Root, "no-such-home")));
    }

    [Fact]
    public void Removing_the_key_leaves_the_rest_and_deletes_a_file_that_is_then_empty()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".gemini"));
        File.WriteAllLines(GeminiCli.EnvFile(env.Root), new[] { "FOO=bar", "GEMINI_API_KEY=" + Key });

        Assert.True(GeminiCli.RemoveApiKey(env.Root));
        Assert.Equal(new[] { "FOO=bar" }, File.ReadAllLines(GeminiCli.EnvFile(env.Root)));
        Assert.False(GeminiCli.RemoveApiKey(env.Root));   // nothing left to remove

        GeminiCli.StoreApiKey(Key, env.Root);
        File.WriteAllText(GeminiCli.EnvFile(env.Root), "GEMINI_API_KEY=" + Key + "\n");
        Assert.True(GeminiCli.RemoveApiKey(env.Root));
        Assert.False(File.Exists(GeminiCli.EnvFile(env.Root)));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        using var env = new TestEnv();

        GeminiCli.StoreApiKey(Key, env.Root);
        GeminiCli.StoreApiKey(Key + "x", env.Root);

        Assert.Empty(Directory.GetFiles(Path.Combine(env.Root, ".gemini"), "*.tmp"));
    }

    [Theory]
    [InlineData("AIzaFAKE_exampleKey_0123456789ghij", true)]
    [InlineData("abcdefghij0123456789", true)]
    [InlineData("short", false)]
    [InlineData("has space in the middle 0123456789", false)]
    [InlineData("quote\"inside0123456789012345", false)]
    [InlineData("new\nline0123456789012345678", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_something_shaped_like_a_key_is_accepted(string? key, bool expected) => Assert.Equal(expected, GeminiCli.IsPlausibleKey(key));

    [Fact]
    public void A_masked_key_shows_only_the_ends()
    {
        Assert.Equal("AIza...ghij", GeminiCli.Mask(Key));
        Assert.DoesNotContain("exampleKey", GeminiCli.Mask(Key));
        Assert.Equal("****", GeminiCli.Mask("abcd"));
    }

    [Fact]
    public void A_stored_key_is_given_to_a_run_only_when_the_environment_has_none()
    {
        using var env = new TestEnv();
        GeminiCli.StoreApiKey(Key, env.Root);

        var empty = new Dictionary<string, string?>();
        GeminiCli.ApplyStoredApiKey(empty, env.Root);
        Assert.Equal(Key, empty["GEMINI_API_KEY"]);

        var hasGemini = new Dictionary<string, string?> { ["GEMINI_API_KEY"] = "from-the-environment-0000" };
        GeminiCli.ApplyStoredApiKey(hasGemini, env.Root);
        Assert.Equal("from-the-environment-0000", hasGemini["GEMINI_API_KEY"]);

        var hasGoogle = new Dictionary<string, string?> { ["GOOGLE_API_KEY"] = "google-var-000000000000" };
        GeminiCli.ApplyStoredApiKey(hasGoogle, env.Root);
        Assert.False(hasGoogle.ContainsKey("GEMINI_API_KEY"));
    }

    [Fact]
    public void The_install_command_is_the_documented_one()
    {
        Assert.Equal("npm install -g @google/gemini-cli", GeminiCli.InstallCommand);
    }
}

public class GeminiLoginWatchTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(30);

    [Fact]
    public async Task A_login_that_appears_while_waiting_is_noticed()
    {
        using var env = new TestEnv();
        var since = DateTime.UtcNow.AddSeconds(-1);
        Directory.CreateDirectory(Path.Combine(env.Root, ".gemini"));
        _ = Task.Run(async () => { await Task.Delay(200); File.WriteAllText(GeminiCli.CredentialsFile(env.Root), "{}"); });

        Assert.True(await GeminiCli.WaitForNewLoginAsync(since, TimeSpan.FromSeconds(5), Poll, env.Root));
    }

    [Fact]
    public async Task An_older_login_is_not_the_one_just_made()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".gemini"));
        File.WriteAllText(GeminiCli.CredentialsFile(env.Root), "{}");
        File.SetLastWriteTimeUtc(GeminiCli.CredentialsFile(env.Root), DateTime.UtcNow.AddDays(-3));

        Assert.False(await GeminiCli.WaitForNewLoginAsync(DateTime.UtcNow.AddSeconds(-1), TimeSpan.FromMilliseconds(250), Poll, env.Root));
    }

    [Fact]
    public async Task Waiting_stops_as_soon_as_the_window_is_gone()
    {
        using var env = new TestEnv();
        var started = DateTime.UtcNow;

        var found = await GeminiCli.WaitForNewLoginAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), Poll, env.Root, stop: () => true);

        Assert.False(found);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Waiting_can_be_cancelled()
    {
        using var env = new TestEnv();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.False(await GeminiCli.WaitForNewLoginAsync(DateTime.UtcNow, TimeSpan.FromSeconds(30), Poll, env.Root, cancellationToken: cts.Token));
    }
}

public class GeminiAccountSignInTests
{
    private const string Key = "AIzaFAKE_exampleKey_0123456789ghij";

    private static GeminiAccountService Service(TestEnv env, Func<string, string?>? environment = null) =>
        new(new SettingsService(env.Paths), env.Root, environment ?? (_ => null));

    [Fact]
    public void The_status_says_where_the_sign_in_comes_from_and_the_environment_wins()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".gemini"));
        File.WriteAllText(GeminiCli.CredentialsFile(env.Root), "{}");
        GeminiCli.StoreApiKey(Key, env.Root);

        var fromFile = Service(env).GetStatus();
        Assert.Equal("api-key-file", fromFile.Source);                 // a key outranks a cached Google login
        Assert.Contains("AIza...ghij", fromFile.Detail);
        Assert.DoesNotContain("exampleKey", fromFile.Detail);          // never the whole key

        var fromEnvironment = Service(env, v => v == "GEMINI_API_KEY" ? "k" : null).GetStatus();
        Assert.Equal("api-key-env", fromEnvironment.Source);

        GeminiCli.RemoveApiKey(env.Root);
        Assert.Equal("google", Service(env).GetStatus().Source);

        GeminiCli.SignOut(env.Root);
        var none = Service(env).GetStatus();
        Assert.False(none.LoggedIn);
        Assert.Equal("", none.Source);
    }

    [Fact]
    public void Saving_a_key_that_does_not_look_like_one_writes_nothing()
    {
        using var env = new TestEnv();

        Assert.Contains("does not look like", Service(env).SaveApiKey("hello world"));
        Assert.Contains("does not look like", Service(env).SaveApiKey(""));
        Assert.False(File.Exists(GeminiCli.EnvFile(env.Root)));
    }

    [Fact]
    public void A_pasted_key_is_trimmed_and_unquoted_before_it_is_saved()
    {
        using var env = new TestEnv();

        Assert.Null(Service(env).SaveApiKey("  \"" + Key + "\"  "));

        Assert.Equal(Key, GeminiCli.ReadStoredApiKey(env.Root)!.Value.Value);
    }

    [Fact]
    public void Removing_the_key_says_when_the_environment_still_supplies_one()
    {
        using var env = new TestEnv();
        GeminiCli.StoreApiKey(Key, env.Root);

        Assert.Null(Service(env).RemoveApiKey());
        Assert.Null(GeminiCli.ReadStoredApiKey(env.Root));
        Assert.Contains("GOOGLE_API_KEY", Service(env, v => v == "GOOGLE_API_KEY" ? "k" : null).RemoveApiKey());
    }

    [Fact]
    public void Signing_out_mentions_a_saved_key_that_keeps_Gemini_working()
    {
        using var env = new TestEnv();
        GeminiCli.StoreApiKey(Key, env.Root);

        Assert.Contains("saved API key is still in use", Service(env).SignOut());
    }

    [Fact]
    public void Installing_is_refused_when_the_CLI_is_already_there_and_sign_in_needs_the_CLI()
    {
        using var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        var service = new GeminiAccountService(settings, env.Root, _ => null);
        Assert.Contains("Install it with the button", service.StartSignIn());   // not installed yet

        var cli = Path.Combine(env.Root, "gemini.cmd");
        File.WriteAllText(cli, "@echo off");
        settings.Save(new AppSettings { GeminiPath = cli });

        Assert.Equal("The Gemini CLI is already installed.", service.StartInstall());
    }
}

public class GeminiKeyCheckerTests
{
    private const string Key = "AIzaFAKE_exampleKey_0123456789ghij";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK);
        public HttpRequestMessage? Last { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Calls++;
            return Task.FromResult(Respond());
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (GeminiKeyChecker Checker, FakeHandler Handler) Create(Func<HttpResponseMessage> respond)
    {
        var handler = new FakeHandler { Respond = respond };
        return (new GeminiKeyChecker(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task An_accepted_key_is_valid_and_travels_in_a_header_not_the_address()
    {
        var (checker, handler) = Create(() => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await checker.CheckAsync(Key, CancellationToken.None);

        Assert.Equal(KeyCheck.Valid, result.Outcome);
        Assert.Equal(Key, handler.Last!.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain(Key, handler.Last.RequestUri!.ToString());
        Assert.StartsWith("https://generativelanguage.googleapis.com/", handler.Last.RequestUri.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_key_is_invalid_with_Googles_reason(HttpStatusCode code)
    {
        var (checker, _) = Create(() => Json(code, "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\"}}"));

        var result = await checker.CheckAsync(Key, CancellationToken.None);

        Assert.Equal(KeyCheck.Invalid, result.Outcome);
        Assert.Contains("API key not valid", result.Message);
        Assert.DoesNotContain(Key, result.Message);
    }

    [Fact]
    public async Task A_refusal_without_a_readable_reason_still_says_so()
    {
        var (checker, _) = Create(() => Json(HttpStatusCode.BadRequest, "<html>oops</html>"));

        var result = await checker.CheckAsync(Key, CancellationToken.None);

        Assert.Equal(KeyCheck.Invalid, result.Outcome);
        Assert.Contains("rejected", result.Message);
    }

    [Fact]
    public async Task A_key_that_is_over_its_quota_is_still_a_real_key()
    {
        var (checker, _) = Create(() => Json((HttpStatusCode)429, "{\"error\":{\"message\":\"quota\"}}"));

        Assert.Equal(KeyCheck.Valid, (await checker.CheckAsync(Key, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Not_reaching_Google_says_nothing_about_the_key()
    {
        var (server, _) = Create(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var (offline, _) = Create(() => throw new HttpRequestException("no route to host"));

        Assert.Equal(KeyCheck.Unreachable, (await server.CheckAsync(Key, CancellationToken.None)).Outcome);
        var result = await offline.CheckAsync(Key, CancellationToken.None);
        Assert.Equal(KeyCheck.Unreachable, result.Outcome);
        Assert.Contains("no route to host", result.Message);
    }
}

public class GeminiKeyEndpointTests
{
    private const string Key = "AIzaFAKE_exampleKey_0123456789ghij";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK);
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Respond());
        }
    }

    private static (SettingsController Controller, FakeHandler Handler, string Home, TestEnv Env) Create(Func<HttpResponseMessage>? respond = null)
    {
        var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        var handler = new FakeHandler();
        if (respond != null) handler.Respond = respond;
        var controller = new SettingsController(
            settings,
            new AccountService(env.Paths, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountService>.Instance),
            new ClaudeAccountService(settings),
            new GeminiAccountService(settings, env.Root, _ => null),
            new GeminiKeyChecker(new HttpClient(handler)),
            env.Paths)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }   // the status endpoint sets a header
        };
        return (controller, handler, env.Root, env);
    }

    private static (bool Ok, string Message) Read(IActionResult result)
    {
        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
        using var doc = JsonDocument.Parse(json);
        var ok = doc.RootElement.GetProperty("ok").GetBoolean();
        var message = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
        return (ok, message);
    }

    [Fact]
    public async Task A_key_Google_accepts_is_saved_and_never_sent_back()
    {
        var (controller, _, home, env) = Create();
        using var _e = env;

        var result = await controller.GeminiSaveKey(Key, CancellationToken.None);

        var (ok, message) = Read(result);
        Assert.True(ok);
        Assert.Contains("accepted and saved", message);
        Assert.Equal(Key, GeminiCli.ReadStoredApiKey(home)!.Value.Value);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(((JsonResult)result).Value));
    }

    [Fact]
    public async Task A_key_Google_refuses_is_not_saved()
    {
        var (controller, _, home, env) = Create(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"API key not valid.\"}}")
        });
        using var _e = env;

        var (ok, message) = Read(await controller.GeminiSaveKey(Key, CancellationToken.None));

        Assert.False(ok);
        Assert.Contains("API key not valid", message);
        Assert.Null(GeminiCli.ReadStoredApiKey(home));
    }

    [Fact]
    public async Task A_key_that_could_not_be_checked_is_saved_with_a_note()
    {
        var (controller, _, home, env) = Create(() => throw new HttpRequestException("offline"));
        using var _e = env;

        var (ok, message) = Read(await controller.GeminiSaveKey(Key, CancellationToken.None));

        Assert.True(ok);
        Assert.Contains("could not be checked", message);
        Assert.NotNull(GeminiCli.ReadStoredApiKey(home));
    }

    [Fact]
    public async Task Something_that_is_not_a_key_never_reaches_Google_or_the_disk()
    {
        var (controller, handler, home, env) = Create();
        using var _e = env;

        var (ok, message) = Read(await controller.GeminiSaveKey("not a key", CancellationToken.None));

        Assert.False(ok);
        Assert.Contains("does not look like", message);
        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(GeminiCli.EnvFile(home)));
    }

    [Fact]
    public async Task Pasting_with_quotes_and_spaces_works_and_removing_the_key_removes_it()
    {
        var (controller, _, home, env) = Create();
        using var _e = env;

        Assert.True(Read(await controller.GeminiSaveKey("  '" + Key + "' ", CancellationToken.None)).Ok);
        Assert.NotNull(GeminiCli.ReadStoredApiKey(home));

        Assert.True(Read(controller.GeminiRemoveKey()).Ok);
        Assert.Null(GeminiCli.ReadStoredApiKey(home));
    }

    [Fact]
    public void The_status_reports_the_source_to_the_page()
    {
        var (controller, _, home, env) = Create();
        using var _e = env;
        GeminiCli.StoreApiKey(Key, home);

        var json = JsonSerializer.Serialize(((JsonResult)controller.GeminiStatus()).Value);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("loggedIn").GetBoolean());
        Assert.Equal("api-key-file", doc.RootElement.GetProperty("source").GetString());
        Assert.DoesNotContain("exampleKey", json);
    }
}
