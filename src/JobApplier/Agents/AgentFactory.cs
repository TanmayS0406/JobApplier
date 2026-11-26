using System.Text.Json.Nodes;
using JobApplier.Claude;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Mcp;
using JobApplier.Services;

namespace JobApplier.Agents;

[Flags]
public enum AgentCapabilities
{
    None = 0,

    /// <summary>The Read tool (confined to the job workspace) - for screenshots and resumes.</summary>
    ReadFiles = 1,

    /// <summary>Playwright MCP: a visible browser window owned by this agent.</summary>
    Browser = 2,

    /// <summary>The app's own MCP tools: wait_for_user, record_account, read_verification_email, report_progress.</summary>
    AppTools = 4,

    /// <summary>Gives the browser this job's generated password as the JOB_PASSWORD secret.</summary>
    PasswordSecret = 8
}

/// <summary>Where the app's MCP endpoint is reachable. Set once Kestrel has started.</summary>
public sealed class McpEndpointInfo
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5189";
}

/// <summary>
/// Builds the agent for a job - Claude Code or the GitHub Copilot CLI, whichever the settings select - with the right workspace, tools and
/// live logging. Both take the same <see cref="ClaudeAgentSpec"/> and produce the same events.
/// </summary>
public sealed class AgentFactory
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly JobStore _store;
    private readonly JobMcpServer _mcp;
    private readonly McpEndpointInfo _endpoint;

    public AgentFactory(AppPaths paths, SettingsService settings, JobStore store, JobMcpServer mcp, McpEndpointInfo endpoint)
    {
        _paths = paths;
        _settings = settings;
        _store = store;
        _mcp = mcp;
        _endpoint = endpoint;
    }

    public CliAgent CreateForJob(Guid jobId, string role, string instructions, AgentCapabilities capabilities,
                                        Action<ClaudeEvent>? onEvent = null)
    {
        var workspace = _paths.EnsureWorkspace(jobId);
        var privateFolder = _paths.EnsurePrivateFolder(jobId);
        var settings = _settings.Current;

        var servers = new Dictionary<string, JsonObject>();
        if (capabilities.HasFlag(AgentCapabilities.Browser))
        {
            string? secretsFile = null;
            if (capabilities.HasFlag(AgentCapabilities.PasswordSecret))
            {
                secretsFile = Path.Combine(privateFolder, "secrets.env");
                var password = _store.Read(jobId, r => r.Password)
                    ?? throw new InvalidOperationException("No password has been generated for this job.");
                // Quoted: in dotenv an unquoted '#' would end the value.
                File.WriteAllText(secretsFile, $"{Prompts.PasswordSecretName}=\"{password}\"{Environment.NewLine}");
            }
            servers["playwright"] = PlaywrightServer(settings, secretsFile);
        }

        if (capabilities.HasFlag(AgentCapabilities.AppTools))
        {
            servers["jobapplier"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"{_endpoint.BaseUrl.TrimEnd('/')}/mcp/{jobId}",
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + _mcp.Token }
            };
        }

        var spec = new ClaudeAgentSpec
        {
            Name = role,
            Description = $"Claude Code agent ({role})",
            Instructions = instructions,
            WorkingDirectory = workspace,
            PrivateFolder = privateFolder,
            ClaudePath = Provider(settings).Path,
            Model = Provider(settings).Model,
            Effort = Provider(settings).Effort,
            BuiltInTools = capabilities.HasFlag(AgentCapabilities.ReadFiles) ? new[] { "Read" } : Array.Empty<string>(),
            McpServers = servers,
            OnEvent = evt =>
            {
                LogEvent(jobId, evt);
                onEvent?.Invoke(evt);
            }
        };
        return Create(spec, settings);
    }

    /// <summary>An agent that is not tied to a job (resume import). Reads files in <paramref name="workspace"/> only.</summary>
    public CliAgent CreateStandalone(string role, string instructions, string workspace, string privateFolder)
    {
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(privateFolder);
        var settings = _settings.Current;
        return Create(new ClaudeAgentSpec
        {
            Name = role,
            Description = $"Claude Code agent ({role})",
            Instructions = instructions,
            WorkingDirectory = workspace,
            PrivateFolder = privateFolder,
            ClaudePath = Provider(settings).Path,
            Model = Provider(settings).Model,
            Effort = Provider(settings).Effort,
            BuiltInTools = new[] { "Read" }
        }, settings);
    }

    /// <summary>The CLI path, model and effort of the selected provider. The spec fields keep their Claude-era names but hold the selected provider's values.</summary>
    private static (string Path, string Model, string Effort) Provider(AppSettings s) =>
        AiProvider.Normalize(s.Provider) == AiProvider.Copilot ? (s.CopilotPath, s.CopilotModel, "") : (s.ClaudePath, s.Model, s.Effort);

    private static CliAgent Create(ClaudeAgentSpec spec, AppSettings s) =>
        AiProvider.Normalize(s.Provider) == AiProvider.Copilot ? new CopilotCliAgent(spec) : new ClaudeCodeAgent(spec);

    private static JsonObject PlaywrightServer(AppSettings settings, string? secretsFile)
    {
        var args = new List<string>();
        string command;
        if (OperatingSystem.IsWindows())
        {
            command = "cmd";
            args.AddRange(new[] { "/c", "npx" });
        }
        else
        {
            command = "npx";
        }

        args.AddRange(new[] { "-y", "@playwright/mcp@" + settings.PlaywrightMcpVersion });
        args.AddRange(new[] { "--isolated", "--browser", settings.Browser, "--ignore-roots", "--snapshot-mode", "none" });
        if (settings.Headless) args.Add("--headless");
        if (secretsFile != null) args.AddRange(new[] { "--secrets", secretsFile });

        return new JsonObject
        {
            ["command"] = command,
            ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray())
        };
    }

    /// <summary>Writes what the agent does to the job's activity log.</summary>
    private void LogEvent(Guid jobId, ClaudeEvent evt)
    {
        switch (evt)
        {
            case InitEvent init:
                foreach (var (name, status) in init.McpServers.Where(s => s.Status != "connected"))
                    _store.Log(jobId, "warn", $"Tool server '{name}' is {status}.");
                break;

            case NoticeEvent notice:
                _store.Log(jobId, "warn", notice.Text.Length > 300 ? notice.Text[..300] + "..." : notice.Text);
                break;

            case TextEvent text:
                var line = text.Text.Trim();
                if (line.Length > 0) _store.Log(jobId, "agent", line.Length > 500 ? line[..500] + "..." : line);
                break;

            case ToolUseEvent tool:
                var description = ToolActivity.Describe(tool.Name, tool.Input);
                if (description != null) _store.Log(jobId, "tool", description);
                break;

            case ResultEvent { Denials.Count: > 0 } result:
                _store.Log(jobId, "warn", "Blocked by the safety rules: " + string.Join("; ", result.Denials.Distinct()));
                break;
        }
    }
}
