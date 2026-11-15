using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace JobApplier.Claude;

/// <summary>
/// The Claude Code CLI as an agent, signed in with the user's own Claude account (OAuth, no API key). Each run starts
/// <c>claude -p</c> in the agent's workspace and connects it to the MCP servers named in the <see cref="ClaudeAgentSpec"/> -
/// Playwright for the browser, and the app's own server for asking the user for help and saving accounts. Typed results use
/// the CLI's own <c>--json-schema</c>.
/// </summary>
public sealed class ClaudeCodeAgent : CliAgent
{
    public ClaudeCodeAgent(ClaudeAgentSpec spec) : base(spec) { }

    protected override async IAsyncEnumerable<ClaudeEvent> ExecuteAsync(
        string prompt, string? schema, ClaudeAgentSession? claudeSession, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cliPath = ClaudeCli.Locate(Spec.ClaudePath)
            ?? throw new ClaudeAgentException("Claude Code CLI not found. Install it from https://claude.com/claude-code, or set its path in Settings.");

        var info = ClaudeCli.CreateStartInfo(cliPath, BuildArguments(schema, claudeSession?.ClaudeSessionId), Spec.WorkingDirectory);
        using var process = ClaudeCli.Start(info);
        using var kill = cancellationToken.Register(() => ClaudeCli.KillTree(process));

        // The prompt goes through stdin: no command-line length or quoting limits.
        await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            await stdin.WriteAsync(prompt.AsMemory(), cancellationToken);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        ResultEvent? result = null;

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
            {
                foreach (var evt in ClaudeStreamParser.Parse(line))
                {
                    if (evt is InitEvent init && claudeSession != null) claudeSession.ClaudeSessionId = init.SessionId ?? claudeSession.ClaudeSessionId;
                    if (evt is ResultEvent r)
                    {
                        result = r;
                        if (claudeSession != null) claudeSession.ClaudeSessionId = r.SessionId ?? claudeSession.ClaudeSessionId;
                    }

                    Spec.OnEvent?.Invoke(evt);
                    yield return evt;
                }
            }
        }
        finally
        {
            // Playwright MCP and the browser are children of this process - make sure they go with it.
            if (!process.HasExited) ClaudeCli.KillTree(process);
        }

        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        var stderr = (await stderrTask).Trim();

        if (result is { IsError: true })
            throw Describe(result.Text ?? stderr);
        if (result == null)
            throw Describe(stderr.Length > 0 ? stderr : $"Claude Code exited with code {process.ExitCode} without a result.");
    }

    private List<string> BuildArguments(string? jsonSchema, string? resumeSessionId)
    {
        Directory.CreateDirectory(Spec.PrivateFolder);

        var promptFile = Path.Combine(Spec.PrivateFolder, $"{Safe(Spec.Name)}-system-prompt.txt");
        File.WriteAllText(promptFile, Spec.Instructions, new UTF8Encoding(false));

        var mcpServers = new JsonObject();
        foreach (var (name, definition) in Spec.McpServers) mcpServers[name] = definition.DeepClone();
        var mcpFile = Path.Combine(Spec.PrivateFolder, $"{Safe(Spec.Name)}-mcp.json");
        File.WriteAllText(mcpFile, new JsonObject { ["mcpServers"] = mcpServers }.ToJsonString(), new UTF8Encoding(false));

        var allowed = Spec.BuiltInTools.Concat(Spec.McpServers.Keys.Select(n => "mcp__" + n)).ToList();

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--strict-mcp-config",                       // only the servers below, never the user's own MCP setup
            "--mcp-config", mcpFile,
            "--restricted",                              // no shell / code tools, file tools confined to the workspace
            "--permission-mode", "dontAsk",              // never wait for an approval prompt nobody can see: deny instead
            "--disable-slash-commands",
            "--tools", string.Join(",", Spec.BuiltInTools),
            "--append-system-prompt-file", promptFile
        };

        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(allowed);
        }
        if (!Spec.PersistSession) args.Add("--no-session-persistence");
        if (!string.IsNullOrWhiteSpace(Spec.Model)) { args.Add("--model"); args.Add(Spec.Model.Trim()); }
        if (!string.IsNullOrWhiteSpace(Spec.Effort)) { args.Add("--effort"); args.Add(Spec.Effort.Trim()); }
        if (jsonSchema != null) { args.Add("--json-schema"); args.Add(jsonSchema); }
        if (Spec.PersistSession && resumeSessionId != null) { args.Add("--resume"); args.Add(resumeSessionId); }
        return args;
    }

    private static string Safe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private static ClaudeAgentException Describe(string error) =>
        AgentErrors.Describe(error, "Open Settings and press 'Sign in with Claude'.");
}
