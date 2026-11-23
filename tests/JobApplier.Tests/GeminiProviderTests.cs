using System.Text.Json;
using System.Text.Json.Nodes;
using JobApplier.Claude;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class GeminiErrorTests
{
    private const string Noisy =
        "Warning: True color (24-bit) support not detected.\nSecurity Warning: Skipping system settings file 'C:\\x\\s.json': Parent directory is insecure\n" +
        "Ripgrep is not available. Falling back to GrepTool.\nAn unexpected critical error occurred: This client is no longer supported for Gemini Code Assist for individuals. " +
        "To continue using Gemini, please migrate to the Antigravity suite of products: https://antigravity.google";

    [Fact]
    public void Start_up_warnings_are_left_out_of_an_error_and_the_real_message_stays()
    {
        var cleaned = GeminiErrors.Clean(Noisy);

        Assert.StartsWith("An unexpected critical error occurred", cleaned);
        Assert.DoesNotContain("True color", cleaned);
        Assert.DoesNotContain("insecure", cleaned);
        Assert.DoesNotContain("Ripgrep", cleaned);
    }

    [Fact]
    public void An_error_that_is_only_warnings_is_kept_as_it_was_rather_than_shown_empty()
    {
        Assert.Equal("Warning: only this", GeminiErrors.Clean("Warning: only this"));
        Assert.Equal("", GeminiErrors.Clean(null));
    }

    [Fact]
    public void Googles_refusal_of_the_personal_sign_in_becomes_an_instruction_to_use_an_API_key()
    {
        GeminiHealth.GoogleSignInRejected = false;

        var ex = GeminiCliAgent.Describe(Noisy);

        Assert.True(ex.IsAuthProblem);
        Assert.False(ex.IsUsageLimit);
        Assert.Contains("API key", ex.Message);
        Assert.Contains("aistudio.google.com/apikey", ex.Message);
        Assert.DoesNotContain("True color", ex.Message);
        Assert.True(GeminiHealth.GoogleSignInRejected);
        GeminiHealth.GoogleSignInRejected = false;
    }

    [Fact]
    public void Other_errors_keep_their_own_meaning_and_get_no_API_key_advice_unless_it_is_about_signing_in()
    {
        Assert.True(GeminiCliAgent.Describe("Please set an Auth method in your settings").IsAuthProblem);
        Assert.False(GeminiCliAgent.Describe("Network unreachable").IsAuthProblem);
    }

    [Fact]
    public void An_API_key_is_recognised_in_the_environment_whichever_variable_holds_it()
    {
        Assert.False(GeminiCli.HasApiKey(new Dictionary<string, string?>()));
        Assert.False(GeminiCli.HasApiKey(new Dictionary<string, string?> { ["GEMINI_API_KEY"] = "  " }));
        Assert.True(GeminiCli.HasApiKey(new Dictionary<string, string?> { ["GEMINI_API_KEY"] = "k" }));
        Assert.True(GeminiCli.HasApiKey(new Dictionary<string, string?> { ["GOOGLE_API_KEY"] = "k" }));
    }

    [Fact]
    public void The_generated_settings_stop_the_CLI_from_switching_the_job_s_MCP_servers_off_as_untrusted()
    {
        var spec = new ClaudeAgentSpec { Name = "n", Instructions = "i", WorkingDirectory = "w", PrivateFolder = "p" };

        var settings = GeminiSettings.Build(spec);

        Assert.False((bool)settings["security"]!["folderTrust"]!["enabled"]!);
    }
}

[Collection("gemini-environment")]
public class GeminiAgentRunTests
{
    // A stand-in for gemini: records where it was told to find its settings, then fails with whatever FAKE_GEMINI_STDERR holds.
    private const string Script = """
        const fs = require('fs'), path = require('path');
        fs.writeFileSync(path.join(__dirname, 'env.json'), JSON.stringify({ home: process.env.GEMINI_CLI_HOME ?? null, system: process.env.GEMINI_CLI_SYSTEM_SETTINGS_PATH ?? null }));
        process.stdin.resume();
        process.stdin.on('end', () => { process.stderr.write(process.env.FAKE_GEMINI_STDERR ?? ''); process.exit(1); });
        """;

    private static bool NodeAvailable() =>
        OperatingSystem.IsWindows() && (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => { try { return File.Exists(Path.Combine(d.Trim(), "node.exe")); } catch (ArgumentException) { return false; } });

    [Fact]
    public async Task With_an_API_key_the_run_gets_its_own_home_holding_the_settings_and_a_refused_sign_in_is_explained()
    {
        if (!NodeAvailable()) return;
        using var env = new TestEnv();
        var folder = Path.Combine(env.Root, "fake-gemini");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fake-gemini.js"), Script);
        var cmd = Path.Combine(folder, "gemini.cmd");
        File.WriteAllText(cmd, "@echo off\r\nnode \"%~dp0fake-gemini.js\" %*\r\n");

        var spec = new ClaudeAgentSpec
        {
            Name = "test", Instructions = "do it", ClaudePath = cmd,
            WorkingDirectory = Path.Combine(env.Root, "ws"), PrivateFolder = Path.Combine(env.Root, "private"),
            McpServers = new Dictionary<string, JsonObject> { ["playwright"] = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("-y", "x") } }
        };
        Directory.CreateDirectory(spec.WorkingDirectory);

        var oldKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var oldStderr = Environment.GetEnvironmentVariable("FAKE_GEMINI_STDERR");
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", "test-key-not-real");
            Environment.SetEnvironmentVariable("FAKE_GEMINI_STDERR", "Warning: noise\nThis client is no longer supported for Gemini Code Assist for individuals.");
            GeminiHealth.GoogleSignInRejected = false;

            var ex = await Assert.ThrowsAsync<ClaudeAgentException>(() => new GeminiCliAgent(spec).RunAsync("task"));

            Assert.True(ex.IsAuthProblem);
            Assert.Contains("API key", ex.Message);

            using var seen = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "env.json")));
            var home = seen.RootElement.GetProperty("home").GetString()!;
            Assert.Equal(Path.Combine(spec.PrivateFolder, "gemini-home"), home);
            Assert.Equal(JsonValueKind.Null, seen.RootElement.GetProperty("system").ValueKind);   // not the system-settings path the CLI ignores

            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ".gemini", "settings.json")))!;
            Assert.NotNull(settings["mcpServers"]!["playwright"]);
            Assert.False((bool)settings["security"]!["folderTrust"]!["enabled"]!);
            Assert.False(home.StartsWith(spec.WorkingDirectory, StringComparison.OrdinalIgnoreCase), "the agent must not be able to read the settings (they can hold tokens)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", oldKey);
            Environment.SetEnvironmentVariable("FAKE_GEMINI_STDERR", oldStderr);
            GeminiHealth.GoogleSignInRejected = false;
        }
    }
}

public class GeminiStatusWarningTests
{
    [Fact]
    public void Only_a_refused_Google_account_sign_in_is_warned_about_and_a_working_run_clears_it()
    {
        try
        {
            GeminiHealth.GoogleSignInRejected = false;
            Assert.Null(GeminiHealth.WarningFor("google"));

            GeminiHealth.GoogleSignInRejected = true;
            Assert.Contains("API key", GeminiHealth.WarningFor("google"));
            Assert.Null(GeminiHealth.WarningFor("api-key-file"));   // a key is never affected by Google's retirement of the personal sign-in
            Assert.Null(GeminiHealth.WarningFor("api-key-env"));
            Assert.Null(GeminiHealth.WarningFor(""));
        }
        finally { GeminiHealth.GoogleSignInRejected = false; }
    }
}

public class GeminiKeyPasteTests
{
    private const string Dotted = "AQ.Ab8RN6FAKE_exampleKey_0123456789";   // newer Google keys contain a dot
    private const string Classic = "AIzaFAKE_exampleKey_0123456789ghij";

    [Theory]
    [InlineData(Dotted, true)]
    [InlineData(Classic, true)]
    [InlineData("a=b0123456789012345678901", false)]
    [InlineData("a:b0123456789012345678901", false)]
    [InlineData("a/b0123456789012345678901", false)]
    public void Keys_of_the_old_and_the_new_shape_are_accepted_and_other_characters_are_not(string key, bool expected) =>
        Assert.Equal(expected, GeminiCli.IsPlausibleKey(key));

    [Theory]
    [InlineData(Dotted)]
    [InlineData("  " + Dotted + "  ")]
    [InlineData("\"" + Dotted + "\"")]
    [InlineData("GEMINI_API_KEY=" + Dotted)]
    [InlineData("GEMINI_API_KEY=\"" + Dotted + "\"")]
    [InlineData("export GEMINI_API_KEY=" + Dotted)]
    [InlineData("export GEMINI_API_KEY=\"" + Dotted + "\"\r\n")]
    [InlineData("set GOOGLE_API_KEY = " + Dotted)]
    [InlineData("gemini_api_key=" + Dotted)]
    public void A_key_is_found_in_whatever_was_copied_with_it(string pasted) =>
        Assert.Equal(Dotted, GeminiCli.NormalizeKey(pasted));

    [Fact]
    public void A_pasted_line_with_the_variable_name_is_saved_as_the_key_alone()
    {
        using var env = new TestEnv();
        var service = new GeminiAccountService(new SettingsService(env.Paths), env.Root, _ => null);

        Assert.Null(service.SaveApiKey("export GEMINI_API_KEY=\"" + Dotted + "\""));

        Assert.Equal(("GEMINI_API_KEY", Dotted), GeminiCli.ReadStoredApiKey(env.Root));
    }

    [Theory]
    [InlineData(null, "nothing was pasted")]
    [InlineData("", "nothing was pasted")]
    [InlineData("two words 0123456789012345", "spaces or line breaks")]
    [InlineData("teVw", "only 4 characters")]
    [InlineData("a=b0123456789012345678901", "characters a key does not have")]
    public void A_pasted_text_that_is_not_a_key_is_told_what_is_wrong_in_words_that_fit_it(string? pasted, string reason)
    {
        var message = GeminiAccountService.KeyShapeError(pasted);

        Assert.StartsWith("That does not look like a Gemini API key", message);
        Assert.Contains(reason, message);
    }

    [Fact]
    public void The_cut_off_end_shown_in_AI_Studio_is_explained_and_never_echoed_back()
    {
        var message = GeminiAccountService.KeyShapeError("teVw")!;

        Assert.Contains("copy button", message);
        Assert.DoesNotContain("teVw", message);   // whatever was pasted might be a key
        Assert.Null(GeminiAccountService.KeyShapeError(Dotted));
    }
}
