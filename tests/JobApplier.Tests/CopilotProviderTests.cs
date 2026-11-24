using System.Text.Json;
using System.Text.Json.Nodes;
using JobApplier.Agents;
using JobApplier.Claude;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Services;
using Microsoft.Agents.AI;

namespace JobApplier.Tests;

public class CopilotStreamTests
{
    private static List<ClaudeEvent> Run(params string[] lines)
    {
        var parser = new CopilotStreamParser();
        return lines.SelectMany(parser.Parse).ToList();
    }

    [Fact]
    public void A_run_becomes_the_same_events_the_Claude_side_produces()
    {
        var events = Run(
            """{"type":"session.start","data":{"sessionId":"abc123"}}""",
            """{"type":"session.warning","data":{"warningType":"telemetry","message":"OpenTelemetry could not be initialized"}}""",
            """{"type":"assistant.message","data":{"content":"Opening the page.","toolRequests":[{"name":"playwright-browser_click","toolCallId":"t1"}]}}""",
            """{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"playwright-browser_click","mcpServerName":"playwright","mcpToolName":"browser_click","arguments":{"element":"Apply"}}}""",
            """{"type":"tool.execution_complete","data":{"toolCallId":"t1","success":true}}""",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"```json\\n{\\\"Found\\\":true,\\\"Url\\\":\\\"https://a.example/1\\\"}\\n```\"}}",
            """{"type":"assistant.usage","data":{"inputTokens":120,"outputTokens":30,"model":"m"}}""",
            """{"type":"result","sessionId":"abc123","exitCode":0,"usage":{"premiumRequests":1}}""");

        Assert.Equal("abc123", Assert.IsType<InitEvent>(events[0]).SessionId);
        Assert.Equal("Opening the page.", Assert.IsType<TextEvent>(events[1]).Text);
        Assert.Equal("mcp__playwright__browser_click", Assert.IsType<ToolUseEvent>(events[2]).Name);
        var result = Assert.IsType<ResultEvent>(events[3]);
        Assert.Equal(4, events.Count);                                  // telemetry noise is dropped, and the final JSON is the answer, not a log line
        Assert.False(result.IsError);
        Assert.Equal("""{"Found":true,"Url":"https://a.example/1"}""", result.StructuredJson);
        Assert.Equal(("abc123", 120L, 30L), (result.SessionId, result.InputTokens, result.OutputTokens));
    }

    [Fact]
    public void A_tool_server_that_failed_to_start_is_reported_for_the_job_log()
    {
        var events = Run("""{"type":"session.warning","data":{"warningType":"mcp","message":"Failed to start MCP Servers: playwright"}}""");

        Assert.Equal("Failed to start MCP Servers: playwright", Assert.IsType<NoticeEvent>(Assert.Single(events)).Text);
    }

    [Fact]
    public void A_failed_run_carries_the_error_the_CLI_gave_and_its_kind()
    {
        var parser = new CopilotStreamParser();
        var events = new[]
        {
            """{"type":"session.error","data":{"errorType":"rate_limit","errorCode":"user_weekly_rate_limited","message":"You have hit your weekly rate limit. It resets in 3h."}}""",
            """{"type":"result","sessionId":"s","exitCode":1,"usage":{"premiumRequests":0}}"""
        }.SelectMany(parser.Parse).ToList();

        var result = Assert.IsType<ResultEvent>(Assert.Single(events));
        Assert.True(result.IsError);
        Assert.Equal("You have hit your weekly rate limit. It resets in 3h.", result.Text);
        Assert.Null(result.StructuredJson);
        Assert.Equal("rate_limit", parser.LastErrorType);
    }

    [Fact]
    public void A_failed_run_with_no_error_event_has_no_text_so_the_agent_falls_back_to_what_the_CLI_wrote_on_stderr()
    {
        var result = Assert.IsType<ResultEvent>(Assert.Single(Run("""{"type":"result","sessionId":"s","exitCode":1}""")));

        Assert.True(result.IsError);
        Assert.Null(result.Text);
    }

    [Fact]
    public void A_plain_answer_without_JSON_is_logged_and_has_no_structured_result()
    {
        var events = Run(
            """{"type":"assistant.message","data":{"content":"All done."}}""",
            """{"type":"result","exitCode":0}""");

        Assert.Equal("All done.", Assert.IsType<TextEvent>(events[0]).Text);
        Assert.Null(Assert.IsType<ResultEvent>(events[1]).StructuredJson);
    }

    [Fact]
    public void Text_said_before_a_tool_call_is_logged_then_and_only_the_text_after_the_last_tool_is_the_answer()
    {
        var events = Run(
            """{"type":"assistant.message","data":{"content":"First I look.","toolRequests":[{"name":"view","toolCallId":"a"}]}}""",
            """{"type":"tool.execution_start","data":{"toolCallId":"a","toolName":"view","arguments":{"path":"x.png"}}}""",
            """{"type":"assistant.message","data":{"content":"{\"a\":1}"}}""",
            """{"type":"result","exitCode":0}""");

        Assert.Equal("First I look.", Assert.IsType<TextEvent>(events[0]).Text);
        Assert.IsType<ToolUseEvent>(events[1]);
        Assert.Equal("""{"a":1}""", Assert.IsType<ResultEvent>(events[2]).StructuredJson);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"something_new","data":{}}""")]
    [InlineData("""{"type":"assistant.message_delta","data":{"deltaContent":"x"}}""")]
    public void Lines_that_are_not_events_we_use_are_ignored(string line) => Assert.Empty(Run(line));

    [Fact]
    public void A_password_typed_through_Copilot_is_still_detected()
    {
        var evt = Assert.IsType<ToolUseEvent>(Run(
            """{"type":"tool.execution_start","data":{"toolCallId":"t","toolName":"playwright-browser_type","mcpServerName":"playwright","mcpToolName":"browser_type","arguments":{"element":"Password","text":"JOB_PASSWORD"}}}""").Single());

        Assert.True(ApplyExecutor.TypesPassword(evt));
    }

    [Fact]
    public void The_file_viewer_shows_only_the_file_name_in_the_log()
    {
        var evt = Assert.IsType<ToolUseEvent>(Run(
            """{"type":"tool.execution_start","data":{"toolCallId":"t","toolName":"view","arguments":{"path":"C:\\jobs\\ab\\workspace\\screenshot.png"}}}""").Single());

        Assert.Equal("Read", evt.Name);
        Assert.Equal("Read: screenshot.png", ToolActivity.Describe(evt.Name, evt.Input));
    }

    [Fact]
    public void A_tool_the_rules_refuse_is_reported_as_blocked_instead_of_vanishing()
    {
        var events = Run(
            """{"type":"tool.execution_start","data":{"toolCallId":"t","toolName":"shell","arguments":{"command":"dir"}}}""",
            """{"type":"tool.execution_complete","data":{"toolCallId":"t","success":false,"error":{"code":"denied","message":"Permission denied by the run's rules"}}}""",
            """{"type":"result","exitCode":0}""");

        var result = Assert.IsType<ResultEvent>(events.Last());
        Assert.Single(result.Denials);
        Assert.Contains("shell", result.Denials[0]);
    }

    [Fact]
    public void A_tool_that_failed_for_another_reason_is_not_called_a_denial()
    {
        var events = Run(
            """{"type":"tool.execution_start","data":{"toolCallId":"t","toolName":"playwright-browser_click","mcpServerName":"playwright","mcpToolName":"browser_click","arguments":{}}}""",
            """{"type":"tool.execution_complete","data":{"toolCallId":"t","success":false,"error":{"message":"Element not found"}}}""",
            """{"type":"result","exitCode":0}""");

        Assert.Empty(Assert.IsType<ResultEvent>(events.Last()).Denials);
    }
}

public class CopilotToolNameTests
{
    [Theory]
    [InlineData("playwright-browser_click", "playwright", "browser_click", "mcp__playwright__browser_click")]
    [InlineData("anything", "playwright", "playwright-browser_click", "mcp__playwright__browser_click")]    // the server's name repeated in front of the tool
    [InlineData("wait_for_user", "jobapplier", "wait_for_user", "mcp__jobapplier__wait_for_user")]
    [InlineData("playwright-browser_type", null, null, "mcp__playwright__browser_type")]                     // no server given: ours is recognised by name
    [InlineData("jobapplier-record_account", null, null, "mcp__jobapplier__record_account")]
    [InlineData("report_progress", null, null, "mcp__jobapplier__report_progress")]
    [InlineData("view", null, null, "Read")]
    [InlineData("shell", null, null, "shell")]
    public void Tool_names_are_mapped_to_the_spelling_the_rest_of_the_app_knows(string tool, string? server, string? serverTool, string expected) =>
        Assert.Equal(expected, CopilotToolNames.Normalize(tool, server, serverTool, null).Name);
}

public class CopilotMcpConfigTests
{
    private static ClaudeAgentSpec Spec(params (string Name, JsonObject Definition)[] servers) => new()
    {
        Name = "applicant",
        Instructions = "x",
        WorkingDirectory = "w",
        PrivateFolder = "p",
        McpServers = servers.ToDictionary(s => s.Name, s => s.Definition)
    };

    [Fact]
    public void The_apps_http_server_and_Playwright_are_converted_to_the_CLIs_spelling_with_every_tool_exposed()
    {
        var spec = Spec(
            ("jobapplier", JsonNode.Parse("""{"type":"http","url":"http://127.0.0.1:5189/mcp/ab","headers":{"Authorization":"Bearer t0ken"}}""")!.AsObject()),
            ("playwright", JsonNode.Parse("""{"command":"cmd","args":["/c","npx","-y","@playwright/mcp@0.0.85"]}""")!.AsObject()));

        var servers = CopilotMcpConfig.Build(spec)["mcpServers"]!;

        var app = servers["jobapplier"]!;
        Assert.Equal("http", (string?)app["type"]);
        Assert.Equal("http://127.0.0.1:5189/mcp/ab", (string?)app["url"]);
        Assert.Equal("Bearer t0ken", (string?)app["headers"]!["Authorization"]);
        Assert.Equal(new[] { "*" }, app["tools"]!.AsArray().Select(n => (string?)n));

        var browser = servers["playwright"]!;
        Assert.Equal("local", (string?)browser["type"]);
        Assert.Equal("cmd", (string?)browser["command"]);
        Assert.Equal(4, browser["args"]!.AsArray().Count);
        Assert.Equal(new[] { "*" }, browser["tools"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void Only_the_servers_of_the_job_are_in_the_config()
    {
        var spec = Spec(("jobapplier", JsonNode.Parse("""{"type":"http","url":"http://x"}""")!.AsObject()));

        Assert.Equal(new[] { "jobapplier" }, CopilotMcpConfig.Build(spec)["mcpServers"]!.AsObject().Select(p => p.Key));
    }
}

public class CopilotPromptAndRulesTests
{
    private static ClaudeAgentSpec Spec(IReadOnlyList<string> builtIn, params string[] servers) => new()
    {
        Name = "n", Instructions = "i", WorkingDirectory = "w", PrivateFolder = "p",
        BuiltInTools = builtIn,
        McpServers = servers.ToDictionary(s => s, _ => new JsonObject())
    };

    [Fact]
    public void The_prompt_carries_the_instructions_the_tool_notes_the_task_and_for_a_typed_result_the_schema()
    {
        var typed = CopilotCliAgent.BuildPrompt("BE CAREFUL", "Read the image.", "{\"type\":\"object\"}");
        var free = CopilotCliAgent.BuildPrompt("BE CAREFUL", "Read the image.", null);

        Assert.True(typed.IndexOf("BE CAREFUL", StringComparison.Ordinal) < typed.IndexOf("=== NOTES ABOUT YOUR TOOLS ===", StringComparison.Ordinal));
        Assert.True(typed.IndexOf("=== NOTES ABOUT YOUR TOOLS ===", StringComparison.Ordinal) < typed.IndexOf("Read the image.", StringComparison.Ordinal));
        Assert.Contains("=== RESULT FORMAT ===", typed);
        Assert.Contains("{\"type\":\"object\"}", typed);
        Assert.DoesNotContain("RESULT FORMAT", free);
    }

    [Fact]
    public void Only_the_jobs_own_servers_and_read_only_file_viewing_are_allowed()
    {
        Assert.Equal(new[] { "playwright", "jobapplier", "read" }, CopilotCliAgent.AllowRules(Spec(new[] { "Read" }, "playwright", "jobapplier")));
        Assert.Equal(new[] { "jobapplier" }, CopilotCliAgent.AllowRules(Spec(Array.Empty<string>(), "jobapplier")));
    }

    [Fact]
    public void Shell_file_writes_urls_and_memory_are_denied_outright()
    {
        foreach (var kind in new[] { "shell", "write", "url", "memory" }) Assert.Contains(kind, CopilotCliAgent.DeniedKinds);
    }
}

public class CopilotErrorTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_CLIs_list_of_things_to_try_is_left_out_of_an_error_and_the_real_message_stays()
    {
        var cleaned = CopilotErrors.Clean(
            "Error: Failed to load models\n\nError: GitHub credential authority error: GitHub CLI could not provide a token\n\n" +
            "To resolve this, try the following:\n  • Start 'copilot' and run the '/login' command to re-authenticate");

        Assert.Contains("GitHub credential authority error", cleaned);
        Assert.DoesNotContain("To resolve this", cleaned);
        Assert.DoesNotContain("/login", cleaned);
    }

    [Fact]
    public void An_error_that_is_only_advice_is_kept_as_it_was_rather_than_shown_empty()
    {
        Assert.Equal("", CopilotErrors.Clean(null));
        Assert.Equal("Something", CopilotErrors.Clean("Something"));
    }

    [Theory]
    [InlineData("rate_limit")]
    [InlineData("quota")]
    public void The_CLIs_own_limit_kinds_mean_waiting_fixes_it_whatever_the_wording(string errorType)
    {
        var ex = CopilotCliAgent.Describe("Request refused.", errorType, Now);

        Assert.True(ex.IsUsageLimit);
        Assert.False(ex.IsAuthProblem);
    }

    [Fact]
    public void A_limits_reset_time_is_read_from_the_message()
    {
        var ex = CopilotCliAgent.Describe("You have hit your weekly limit. Try again in 2h.", "rate_limit", Now);

        Assert.Equal(Now.AddSeconds(7201), ex.RetryAfterUtc);
    }

    [Theory]
    [InlineData("authentication")]
    [InlineData("authorization")]
    [InlineData("policy")]
    public void Sign_in_and_access_kinds_point_at_the_sign_in_button(string errorType)
    {
        var ex = CopilotCliAgent.Describe("Access denied.", errorType, Now);

        Assert.True(ex.IsAuthProblem);
        Assert.False(ex.IsUsageLimit);
        Assert.EndsWith(CopilotErrors.SignInHint, ex.Message);
    }

    [Theory]
    [InlineData("Error: No authentication information found")]
    [InlineData("Error: Authentication failed. Your GitHub token may be invalid, expired, or lacking the required permissions.")]
    [InlineData("GitHub credential authority error: GitHub CLI could not provide a token for the selected account")]
    [InlineData("Error: Access denied by policy settings")]
    public void Without_an_error_kind_the_wording_of_a_sign_in_problem_is_recognised(string error)
    {
        var ex = CopilotCliAgent.Describe(error, null, Now);

        Assert.True(ex.IsAuthProblem);
        Assert.EndsWith(CopilotErrors.SignInHint, ex.Message);
    }

    [Theory]
    [InlineData("You have used all of your premium requests for this month.")]
    [InlineData("Your AI credits are exhausted.")]
    public void Copilots_quota_wording_is_recognised_as_a_limit_when_only_text_is_left(string error)
    {
        Assert.True(UsageLimit.IsLimit(error));
        Assert.True(CopilotCliAgent.Describe(error, null, Now).IsUsageLimit);
    }

    [Fact]
    public void Anything_else_keeps_its_own_meaning()
    {
        var ex = CopilotCliAgent.Describe("Network unreachable", null, Now);

        Assert.False(ex.IsAuthProblem);
        Assert.False(ex.IsUsageLimit);
        Assert.Equal("Network unreachable", ex.Message);
    }
}

public class CopilotAccountTests
{
    private const string FakeOAuthToken = "gho_0123456789ABCDEFGHIJ0123456789abcdef";

    private static CopilotAccountService Service(TestEnv env, Func<string, string?>? environment = null, params string[] credentialTargets) =>
        new(new SettingsService(env.Paths), configFolder: Path.Combine(env.Root, ".copilot"), environment ?? (_ => null), () => credentialTargets);

    [Fact]
    public void A_token_variable_counts_as_signed_in_and_names_the_variable_but_never_the_token()
    {
        using var env = new TestEnv();

        var status = Service(env, v => v == "GH_TOKEN" ? FakeOAuthToken : null).GetStatus();

        Assert.True(status.LoggedIn);
        Assert.Equal("token-env", status.Source);
        Assert.Contains("GH_TOKEN", status.Detail);
        Assert.DoesNotContain(FakeOAuthToken, status.Detail);
    }

    [Fact]
    public void The_first_variable_in_the_CLIs_own_order_is_the_one_named()
    {
        Assert.Equal("COPILOT_GITHUB_TOKEN", CopilotCli.TokenVariable(v => v is "GITHUB_TOKEN" or "COPILOT_GITHUB_TOKEN" ? "x" : null));
        Assert.Equal("GH_TOKEN", CopilotCli.TokenVariable(v => v is "GITHUB_TOKEN" or "GH_TOKEN" ? "x" : null));
        Assert.Null(CopilotCli.TokenVariable(v => "  "));
    }

    [Fact]
    public void An_entry_of_the_CLI_in_the_credential_store_counts_as_signed_in()
    {
        using var env = new TestEnv();

        var status = Service(env, null, "Target: LegacyGeneric:target=other-app", "Target: LegacyGeneric:target=copilot-cli/https://github.com:octocat").GetStatus();

        Assert.True(status.LoggedIn);
        Assert.Equal("stored", status.Source);
    }

    [Fact]
    public void A_token_in_the_CLIs_plain_text_config_counts_and_is_never_shown()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".copilot"));
        File.WriteAllText(Path.Combine(env.Root, ".copilot", "config.json"), "{\"copilot_tokens\":{\"https://github.com:octocat\":\"" + FakeOAuthToken + "\"}}");

        var status = Service(env).GetStatus();

        Assert.True(status.LoggedIn);
        Assert.Equal("stored", status.Source);
        Assert.DoesNotContain(FakeOAuthToken, status.Detail);
    }

    [Fact]
    public void A_config_without_a_token_and_an_empty_store_is_not_signed_in()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".copilot"));
        File.WriteAllText(Path.Combine(env.Root, ".copilot", "config.json"), """{"firstLaunchAt":"2026-11-24T10:39:11Z"}""");

        var status = Service(env).GetStatus();

        Assert.False(status.LoggedIn);
        Assert.Equal("Not signed in.", status.Detail);
    }

    [Fact]
    public void A_classic_personal_access_token_in_the_config_is_not_a_login()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.Root, ".copilot"));
        File.WriteAllText(Path.Combine(env.Root, ".copilot", "config.json"), "{\"t\":\"ghp_0123456789ABCDEFGHIJ0123456789abcdef\"}");   // the CLI does not accept classic tokens

        Assert.False(Service(env).GetStatus().LoggedIn);
    }

    [Fact]
    public void Signing_in_needs_the_CLI_and_says_how_to_get_it()
    {
        using var env = new TestEnv();
        var settings = new SettingsService(env.Paths);
        var missing = new CopilotAccountService(settings, Path.Combine(env.Root, ".copilot"), _ => null, () => Array.Empty<string>());
        if (missing.CliPath != null) return;   // a real CLI on this PC's PATH: nothing to assert about its absence

        Assert.Contains("not found", missing.StartSignIn());
        Assert.Contains("not found", missing.StartSignOut());
    }

    [Fact]
    public void A_sign_in_from_a_token_variable_cannot_be_signed_out_from_here_and_the_message_says_why()
    {
        using var env = new TestEnv();
        var cli = Path.Combine(env.Root, "copilot.cmd");
        File.WriteAllText(cli, "@echo off");
        var settings = new SettingsService(env.Paths);
        settings.Save(new AppSettings { CopilotPath = cli });
        var service = new CopilotAccountService(settings, Path.Combine(env.Root, ".copilot"), v => v == "COPILOT_GITHUB_TOKEN" ? FakeOAuthToken : null, () => Array.Empty<string>());

        var message = service.StartSignOut();

        Assert.Contains("COPILOT_GITHUB_TOKEN", message);
        Assert.DoesNotContain(FakeOAuthToken, message);
    }

    [Fact]
    public void A_configured_path_is_used_only_if_it_exists()
    {
        using var env = new TestEnv();
        var real = Path.Combine(env.Root, "copilot.cmd");
        File.WriteAllText(real, "@echo off");

        Assert.Equal(real, CopilotCli.Locate(real));
        Assert.Null(CopilotCli.Locate(Path.Combine(env.Root, "missing.cmd")));
    }

    [Fact]
    public void The_users_own_MCP_servers_are_listed_so_a_job_can_switch_them_off_and_a_broken_config_is_not_an_error()
    {
        using var env = new TestEnv();
        var folder = Path.Combine(env.Root, ".copilot");
        Directory.CreateDirectory(folder);

        Assert.Empty(CopilotCli.UserMcpServerNames(folder));                                 // no file

        File.WriteAllText(Path.Combine(folder, "mcp-config.json"), """{"mcpServers":{"github-extra":{"type":"http","url":"https://x"},"notes":{"command":"n"}}}""");
        Assert.Equal(new[] { "github-extra", "notes" }, CopilotCli.UserMcpServerNames(folder));

        File.WriteAllText(Path.Combine(folder, "mcp-config.json"), "{ this is not json");
        Assert.Empty(CopilotCli.UserMcpServerNames(folder));
    }
}

/// <summary>A stand-in for the <c>copilot</c> command: records how it was called and prints a scenario, so the agent can be run without a GitHub account.</summary>
internal static class FakeCopilot
{
    private const string Script = """
        const fs = require('fs'), path = require('path');
        const dir = __dirname;
        const args = process.argv.slice(2);
        fs.writeFileSync(path.join(dir, 'args.json'), JSON.stringify(args));
        const prompt = args[args.indexOf('-p') + 1] || '';
        const mention = /@(\S+\.md)/.exec(prompt);
        if (mention) {
            try { fs.writeFileSync(path.join(dir, 'task.txt'), fs.readFileSync(path.join(process.cwd(), mention[1]), 'utf8')); }
            catch (e) { fs.writeFileSync(path.join(dir, 'task-error.txt'), String(e)); }
        }
        const mcp = args.find(a => a.startsWith('@') && a.endsWith('.json'));
        if (mcp) { try { fs.writeFileSync(path.join(dir, 'mcp.json'), fs.readFileSync(mcp.slice(1), 'utf8')); } catch (e) { /* recorded as missing */ } }
        const scenario = JSON.parse(fs.readFileSync(path.join(dir, 'scenario.json'), 'utf8'));
        for (const line of scenario.stdout) console.log(JSON.stringify(line));
        if (scenario.stderr) process.stderr.write(scenario.stderr);
        process.exit(scenario.exit ?? 0);
        """;

    public static bool Available =>
        OperatingSystem.IsWindows() && (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(d => { try { return File.Exists(Path.Combine(d.Trim(), "node.exe")); } catch (ArgumentException) { return false; } });

    /// <summary>Puts the fake in a folder of its own as <c>copilot.cmd</c> and returns its path.</summary>
    public static string Install(TestEnv env, object stdout, string? stderr = null, int exit = 0, string folderName = "fake-copilot")
    {
        var folder = Path.Combine(env.Root, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fake-copilot.js"), Script);
        File.WriteAllText(Path.Combine(folder, "scenario.json"), JsonSerializer.Serialize(new { stdout, stderr, exit }));
        var cmd = Path.Combine(folder, "copilot.cmd");
        File.WriteAllText(cmd, "@echo off\r\nnode \"%~dp0fake-copilot.js\" %*\r\n");
        return cmd;
    }

    public static string[] Args(string cli) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(cli)!, "args.json")))!;

    public static string TaskSeen(string cli) => File.ReadAllText(Path.Combine(Path.GetDirectoryName(cli)!, "task.txt"));

    public static string McpSeen(string cli) => File.ReadAllText(Path.Combine(Path.GetDirectoryName(cli)!, "mcp.json"));

    public static bool WasRun(string cli) => File.Exists(Path.Combine(Path.GetDirectoryName(cli)!, "args.json"));

    // ---- scenario lines ----

    public static object Start(string id = "s-1") => new { type = "session.start", data = new { sessionId = id } };

    public static object Say(string text, bool thenTools = false) =>
        new { type = "assistant.message", data = new { content = text, toolRequests = thenTools ? new object[] { new { name = "x" } } : Array.Empty<object>() } };

    public static object Tool(string server, string tool, object arguments, string id = "t1") =>
        new { type = "tool.execution_start", data = new { toolCallId = id, toolName = server + "-" + tool, mcpServerName = server, mcpToolName = tool, arguments } };

    public static object Done(int exitCode = 0, string id = "s-1") => new { type = "result", sessionId = id, exitCode, usage = new { premiumRequests = 1 } };
}

public class CopilotAgentRunTests
{
    private const string FoundJson =
        """{"Found":true,"Url":"https://a.example/1","Portal":"Greenhouse","Company":"Acme","Title":"Dev","Location":"Berlin","Description":"d","Confidence":"high","Notes":""}""";

    private static ClaudeAgentSpec Spec(TestEnv env, string cli, List<ClaudeEvent>? events = null) => new()
    {
        Name = "finder",
        Instructions = "BE CAREFUL",
        ClaudePath = cli,
        WorkingDirectory = Path.Combine(env.Root, "ws"),
        PrivateFolder = Path.Combine(env.Root, "private"),
        BuiltInTools = new[] { "Read" },
        McpServers = new Dictionary<string, JsonObject>
        {
            ["playwright"] = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("-y", "@playwright/mcp@0.0.85") },
            ["jobapplier"] = new JsonObject { ["type"] = "http", ["url"] = "http://127.0.0.1:5189/mcp/ab", ["headers"] = new JsonObject { ["Authorization"] = "Bearer SECRET-TOKEN" } }
        },
        OnEvent = events == null ? null : events.Add
    };

    private static async Task<FoundPosting> RunTyped(CopilotCliAgent agent, string prompt)
    {
        var response = await agent.RunAsync<FoundPosting>(prompt, session: null, serializerOptions: JsonFile.Options, options: null, cancellationToken: CancellationToken.None);
        return response.Result;
    }

    [Fact]
    public async Task A_run_decodes_the_events_returns_the_typed_result_and_starts_the_CLI_locked_down()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env, new[]
        {
            FakeCopilot.Start("s-9"),
            FakeCopilot.Say("Looking.", thenTools: true),
            FakeCopilot.Tool("playwright", "browser_click", new { element = "Apply" }),
            FakeCopilot.Say(FoundJson),
            FakeCopilot.Done(0, "s-9")
        });
        var events = new List<ClaudeEvent>();
        var spec = Spec(env, cli, events);
        Directory.CreateDirectory(spec.WorkingDirectory);

        var found = await RunTyped(new CopilotCliAgent(spec), "Find the posting for Acme.");

        Assert.True(found.Found);
        Assert.Equal("https://a.example/1", found.Url);
        Assert.Equal("s-9", Assert.IsType<InitEvent>(events[0]).SessionId);
        Assert.Contains(events, e => e is ToolUseEvent { Name: "mcp__playwright__browser_click" });

        var args = FakeCopilot.Args(cli);
        Assert.Contains("--output-format", args);
        Assert.Equal("json", args[Array.IndexOf(args, "--output-format") + 1]);
        foreach (var rule in new[] { "--allow-tool=playwright", "--allow-tool=jobapplier", "--allow-tool=read",
                                     "--deny-tool=shell", "--deny-tool=write", "--deny-tool=url", "--deny-tool=memory" })
            Assert.Contains(rule, args);
        foreach (var flag in new[] { "--no-ask-user", "--no-custom-instructions", "--no-auto-update", "--disable-builtin-mcps", "--disallow-temp-dir" })
            Assert.Contains(flag, args);
        Assert.DoesNotContain(args, a => a is "--allow-all" or "--yolo" or "--allow-all-tools" or "--allow-all-paths" or "--allow-all-urls");
        Assert.Contains(args, a => a.StartsWith("--secret-env-vars=", StringComparison.Ordinal) && a.Contains("GH_TOKEN"));

        // The prompt only names the task file: no instructions, resume or token on the command line.
        var prompt = args[Array.IndexOf(args, "-p") + 1];
        Assert.Contains("@finder-task.md", prompt);
        Assert.DoesNotContain("BE CAREFUL", prompt);
        Assert.DoesNotContain("SECRET-TOKEN", string.Join(" ", args));
    }

    [Fact]
    public async Task The_task_file_holds_the_instructions_the_task_and_the_schema_and_is_removed_afterwards()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env, new[] { FakeCopilot.Start(), FakeCopilot.Say(FoundJson), FakeCopilot.Done() });
        var spec = Spec(env, cli);
        Directory.CreateDirectory(spec.WorkingDirectory);

        await RunTyped(new CopilotCliAgent(spec), "Find the posting for Acme.");

        Assert.False(File.Exists(Path.Combine(spec.WorkingDirectory, "finder-task.md")), "the task file holds the resume and must not stay behind");
        var seen = FakeCopilot.TaskSeen(cli);
        Assert.Contains("BE CAREFUL", seen);
        Assert.Contains("Find the posting for Acme.", seen);
        Assert.Contains("=== RESULT FORMAT ===", seen);
        Assert.Contains("\"Found\"", seen);   // the schema names the properties of FoundPosting
    }

    [Fact]
    public async Task The_MCP_config_with_the_bearer_token_is_in_the_private_folder_out_of_the_agents_reach()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env, new[] { FakeCopilot.Start(), FakeCopilot.Say(FoundJson), FakeCopilot.Done() });
        var spec = Spec(env, cli);
        Directory.CreateDirectory(spec.WorkingDirectory);

        await RunTyped(new CopilotCliAgent(spec), "task");

        var args = FakeCopilot.Args(cli);
        var configArg = args[Array.IndexOf(args, "--additional-mcp-config") + 1];
        Assert.StartsWith("@", configArg);
        var configPath = configArg[1..];
        Assert.StartsWith(spec.PrivateFolder, configPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(configPath.StartsWith(spec.WorkingDirectory, StringComparison.OrdinalIgnoreCase), "the agent must not be able to read the token");
        Assert.Contains("Bearer SECRET-TOKEN", FakeCopilot.McpSeen(cli));
        Assert.Empty(Directory.GetFiles(spec.WorkingDirectory, "*", SearchOption.AllDirectories).Where(f => File.ReadAllText(f).Contains("SECRET-TOKEN")));
    }

    [Fact]
    public async Task An_image_the_task_names_is_attached_and_one_it_does_not_name_is_not()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env, new[] { FakeCopilot.Start(), FakeCopilot.Say(FoundJson), FakeCopilot.Done() });
        var spec = Spec(env, cli);
        Directory.CreateDirectory(spec.WorkingDirectory);
        File.WriteAllBytes(Path.Combine(spec.WorkingDirectory, "screenshot.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(spec.WorkingDirectory, "other.png"), new byte[] { 2 });

        await RunTyped(new CopilotCliAgent(spec), "Read the image file `screenshot.png`.");

        var args = FakeCopilot.Args(cli);
        var attached = args.Where((a, i) => i > 0 && args[i - 1] == "--attachment").ToList();
        Assert.Equal(new[] { Path.Combine(spec.WorkingDirectory, "screenshot.png") }, attached);
    }

    [Fact]
    public async Task A_model_set_in_Settings_is_passed_on_and_none_means_the_CLIs_own_default()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env, new[] { FakeCopilot.Start(), FakeCopilot.Say(FoundJson), FakeCopilot.Done() });
        var spec = new ClaudeAgentSpec
        {
            Name = "finder", Instructions = "i", ClaudePath = cli, Model = " auto ",
            WorkingDirectory = Path.Combine(env.Root, "ws"), PrivateFolder = Path.Combine(env.Root, "private")
        };
        Directory.CreateDirectory(spec.WorkingDirectory);

        await RunTyped(new CopilotCliAgent(spec), "task");
        Assert.Equal("auto", FakeCopilot.Args(cli)[Array.IndexOf(FakeCopilot.Args(cli), "--model") + 1]);

        var noModel = FakeCopilot.Install(env, new[] { FakeCopilot.Start(), FakeCopilot.Say(FoundJson), FakeCopilot.Done() }, folderName: "fake2");
        await RunTyped(new CopilotCliAgent(new ClaudeAgentSpec
        {
            Name = "finder", Instructions = "i", ClaudePath = noModel, WorkingDirectory = spec.WorkingDirectory, PrivateFolder = spec.PrivateFolder
        }), "task");
        Assert.DoesNotContain("--model", FakeCopilot.Args(noModel));
    }

    [Fact]
    public async Task Not_being_signed_in_is_explained_with_what_the_CLI_wrote_and_the_button_to_press()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env,
            new[] { FakeCopilot.Start(), FakeCopilot.Done(exitCode: 1) },
            stderr: "Error: No authentication information found\nCopilot can be authenticated with GitHub using an OAuth Token\n", exit: 1);
        var spec = Spec(env, cli);
        Directory.CreateDirectory(spec.WorkingDirectory);

        var ex = await Assert.ThrowsAsync<ClaudeAgentException>(() => new CopilotCliAgent(spec).RunAsync("task"));

        Assert.True(ex.IsAuthProblem);
        Assert.Contains("No authentication information found", ex.Message);
        Assert.EndsWith(CopilotErrors.SignInHint, ex.Message);
    }

    [Fact]
    public async Task A_rate_limit_reported_as_an_event_becomes_a_usage_limit_the_job_can_wait_out()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env, new object[]
        {
            FakeCopilot.Start(),
            new { type = "session.error", data = new { errorType = "rate_limit", errorCode = "user_weekly_rate_limited", message = "You have reached your weekly rate limit." } },
            FakeCopilot.Done(exitCode: 1)
        }, exit: 1);
        var spec = Spec(env, cli);
        Directory.CreateDirectory(spec.WorkingDirectory);

        var ex = await Assert.ThrowsAsync<ClaudeAgentException>(() => new CopilotCliAgent(spec).RunAsync("task"));

        Assert.True(ex.IsUsageLimit);
        Assert.False(ex.IsAuthProblem);
    }

    [Fact]
    public async Task An_answer_without_the_asked_for_object_fails_the_step_instead_of_guessing()
    {
        if (!FakeCopilot.Available) return;
        using var env = new TestEnv();
        var cli = FakeCopilot.Install(env, new[] { FakeCopilot.Start(), FakeCopilot.Say("I could not find it, sorry."), FakeCopilot.Done() });
        var spec = Spec(env, cli);
        Directory.CreateDirectory(spec.WorkingDirectory);

        var ex = await Assert.ThrowsAsync<ClaudeAgentException>(() => RunTyped(new CopilotCliAgent(spec), "task"));

        Assert.Contains("did not return the structured result", ex.Message);
        Assert.Contains("check the employer's site", ex.Message);
        Assert.Contains("I could not find it", ex.Message);
    }

    [Fact]
    public async Task A_missing_CLI_says_how_to_install_it()
    {
        using var env = new TestEnv();
        var spec = Spec(env, Path.Combine(env.Root, "nope", "copilot.cmd"));

        var ex = await Assert.ThrowsAsync<ClaudeAgentException>(() => new CopilotCliAgent(spec).RunAsync("task"));

        Assert.Contains("not found", ex.Message);
        Assert.Contains("winget install GitHub.Copilot", ex.Message);
        Assert.Contains("npm install -g @github/copilot", ex.Message);
    }
}
