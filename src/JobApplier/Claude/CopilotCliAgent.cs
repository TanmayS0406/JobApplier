using System.Runtime.CompilerServices;
using System.Text;

namespace JobApplier.Claude;

/// <summary>
/// The GitHub Copilot CLI as an agent, signed in with the user's own GitHub account (OAuth through <c>copilot login</c>, no API key held
/// by this application). It plays the same role as <see cref="ClaudeCodeAgent"/> and produces the same events, so a job can run on either.
/// <para>Differences from the Claude side that this class bridges:</para>
/// <list type="bullet">
/// <item>There is no system-prompt flag, and <c>-p</c> cannot be combined with a piped prompt. The instructions, the task and the result
/// format are written to one file in the workspace, which the prompt names with <c>@</c> (the CLI includes it): no command-line length limit.</item>
/// <item>There is no <c>--json-schema</c>: the schema is put in that file and the JSON object is taken from the answer
/// (<see cref="JsonExtract"/>). An answer without a usable object fails the run instead of guessing.</item>
/// <item>MCP servers come from a config file in the private folder (<see cref="CopilotMcpConfig"/>), passed with
/// <c>--additional-mcp-config @file</c>.</item>
/// <item>There is no <c>--restricted</c>: confinement is permission rules. In <c>-p</c> mode anything that is not allowed is refused rather than
/// asked about, so only the two MCP servers and read-only file viewing are allowed, and shell, file writes, URL access and memory are
/// denied outright (a deny rule wins over everything).</item>
/// <item>Images and documents named in the task are attached with <c>--attachment</c>, which is how the CLI is given a screenshot or a resume.</item>
/// </list>
/// </summary>
public sealed class CopilotCliAgent : CliAgent
{
    private static readonly string[] AttachableExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf" };

    public CopilotCliAgent(ClaudeAgentSpec spec) : base(spec) { }

    protected override async IAsyncEnumerable<ClaudeEvent> ExecuteAsync(
        string prompt, string? schema, ClaudeAgentSession? session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cliPath = CopilotCli.Locate(Spec.ClaudePath)
            ?? throw new ClaudeAgentException("GitHub Copilot CLI not found. Install it with 'winget install GitHub.Copilot' or 'npm install -g @github/copilot', or set its path in Settings.");

        Directory.CreateDirectory(Spec.PrivateFolder);
        Directory.CreateDirectory(Spec.WorkingDirectory);

        // The task file sits in the workspace (the CLI may only read there) and is removed afterwards: it holds the resume and profile text.
        var taskFile = $"{Safe(Spec.Name)}-task.md";
        var taskPath = Path.Combine(Spec.WorkingDirectory, taskFile);
        var taskText = BuildPrompt(Spec.Instructions, prompt, schema);
        File.WriteAllText(taskPath, taskText, new UTF8Encoding(false));

        // The MCP config can hold the app's bearer token, so it stays in the private folder, out of the agent's reach.
        var mcpFile = Path.Combine(Spec.PrivateFolder, $"{Safe(Spec.Name)}-copilot-mcp.json");
        File.WriteAllText(mcpFile, CopilotMcpConfig.Build(Spec).ToJsonString(), new UTF8Encoding(false));

        var info = ClaudeCli.CreateStartInfo(cliPath, BuildArguments(taskFile, mcpFile, AttachmentsIn(Spec.WorkingDirectory, prompt)), Spec.WorkingDirectory);
        info.Environment["NO_COLOR"] = "1";

        var parser = new CopilotStreamParser();
        ResultEvent? result = null;
        string stderr;
        int exitCode;

        try
        {
            using var process = ClaudeCli.Start(info);
            using var kill = cancellationToken.Register(() => ClaudeCli.KillTree(process));
            process.StandardInput.Close();   // the prompt is on the command line; nothing is read from standard input

            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
                {
                    foreach (var evt in parser.Parse(line))
                    {
                        if (evt is InitEvent init && session != null) session.ClaudeSessionId = init.SessionId ?? session.ClaudeSessionId;
                        if (evt is ResultEvent r) result = r;

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
            stderr = (await stderrTask).Trim();
            exitCode = process.ExitCode;
        }
        finally
        {
            try { File.Delete(taskPath); } catch (IOException) { /* removed with the job */ }
        }

        if (result is { IsError: true })
            throw Describe(FirstNonEmpty(parser.LastError, result.Text, stderr) ?? "GitHub Copilot CLI failed.", parser.LastErrorType);
        if (result == null)
            throw Describe(FirstNonEmpty(parser.LastError, stderr) ?? $"GitHub Copilot CLI exited with code {exitCode} without a result.", parser.LastErrorType);
        if (schema != null && result.StructuredJson == null)
            throw new ClaudeAgentException(
                "GitHub Copilot did not return the structured result that was asked for. If this was the Apply step, check the employer's site before retrying: the form may already have been submitted. " +
                "What it said: " + Clip(result.Text, 300));
    }

    /// <summary>
    /// The instructions, the task and, for a typed result, the schema and the rule that the answer is that JSON and nothing else. The notes
    /// translate the instructions (written for Claude Code's tool names) into this CLI's.
    /// </summary>
    internal static string BuildPrompt(string instructions, string task, string? schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine(instructions.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("=== NOTES ABOUT YOUR TOOLS ===");
        sb.AppendLine("- \"The Read tool\" above is your file-viewing tool. Images and documents named in the task are also attached to this request.");
        sb.AppendLine("- You can use only: file viewing inside the working folder, the browser tools (browser_snapshot, browser_click, ...) and the JobApplier tools (wait_for_user, record_account, read_verification_email, report_progress) when the instructions mention them. A tool name may carry its server's name in front (for example playwright-browser_click).");
        sb.AppendLine("- Shell commands, file editing, web fetching and web search tools are switched off, and a call to one is refused. Do not try them; use the browser instead.");
        sb.AppendLine();
        sb.AppendLine("=== TASK ===");
        sb.AppendLine(task.TrimEnd());
        if (schema != null)
        {
            sb.AppendLine();
            sb.AppendLine("=== RESULT FORMAT ===");
            sb.AppendLine("When the task is complete, finish with ONE JSON object and nothing after it: no commentary, no markdown fence. " +
                          "It must be valid for this JSON schema (every property present; use \"\" or [] for what is not known):");
            sb.AppendLine(schema);
        }
        return sb.ToString();
    }

    /// <summary>The permission rules of a run: the allowed MCP servers, read-only file viewing, and the kinds of tool that are denied outright.</summary>
    internal static IReadOnlyList<string> AllowRules(ClaudeAgentSpec spec)
    {
        var rules = spec.McpServers.Keys.ToList();
        if (spec.BuiltInTools.Count > 0) rules.Add("read");
        return rules;
    }

    internal static readonly string[] DeniedKinds = { "shell", "write", "url", "memory" };

    private List<string> BuildArguments(string taskFile, string mcpFile, IReadOnlyList<string> attachments)
    {
        var args = new List<string>
        {
            "-p", $"Carry out the task described in @{taskFile}. It holds your instructions, the task and the format of the result; when the task is complete, give the result in exactly that format.",
            "--output-format", "json",
            "--stream", "off",
            "--no-color",
            "--no-ask-user",                        // the agent asks the user through wait_for_user, never through the CLI
            "--no-custom-instructions",             // no AGENTS.md or similar from the folder, whatever a web page or a previous job left there
            "--no-auto-update",                     // a job must not start by downloading a new CLI
            "--disable-builtin-mcps",               // the CLI's own GitHub server is not part of a job
            "--disallow-temp-dir",                  // file access is the workspace and nothing else
            "--additional-mcp-config", "@" + mcpFile,
            "--secret-env-vars=" + string.Join(",", CopilotCli.TokenVariables)   // the user's GitHub token is not handed on to the browser server
        };

        foreach (var rule in AllowRules(Spec)) args.Add("--allow-tool=" + rule);
        foreach (var kind in DeniedKinds) args.Add("--deny-tool=" + kind);

        // The user's own MCP servers are not part of a job either. A server of theirs with the name of one of ours is replaced by ours.
        foreach (var name in CopilotCli.UserMcpServerNames().Where(n => !Spec.McpServers.ContainsKey(n)))
            args.Add("--disable-mcp-server=" + name);

        foreach (var file in attachments) { args.Add("--attachment"); args.Add(file); }
        if (!string.IsNullOrWhiteSpace(Spec.Model)) { args.Add("--model"); args.Add(Spec.Model.Trim()); }
        return args;
    }

    /// <summary>Images and PDFs in the workspace that the task names: the screenshot of a job, the resume to import.</summary>
    internal static IReadOnlyList<string> AttachmentsIn(string workspace, string prompt)
    {
        try
        {
            return Directory.GetFiles(workspace)
                .Where(f => AttachableExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && prompt.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Safe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "(nothing)" : text.Length <= max ? text : text[..max] + "...";

    /// <summary>
    /// Turns what the CLI reported into the exception the rest of the app understands: a limit (waiting fixes it), a sign-in or access
    /// problem (the user has to), or anything else as it was said. The CLI's own error kind is used when it gave one, the wording when not.
    /// </summary>
    internal static ClaudeAgentException Describe(string error, string? errorType = null, DateTime? nowUtc = null)
    {
        var cleaned = CopilotErrors.Clean(error);
        var now = nowUtc ?? DateTime.UtcNow;

        if (CopilotErrors.IsLimitType(errorType))
            return new ClaudeAgentException(cleaned, isUsageLimit: true, retryAfterUtc: Infrastructure.UsageLimit.ResetTime(cleaned, now));
        if (CopilotErrors.IsAccessType(errorType))
            return new ClaudeAgentException(cleaned + " " + CopilotErrors.SignInHint, isAuthProblem: true);
        return AgentErrors.Describe(cleaned, CopilotErrors.SignInHint, now);
    }
}
