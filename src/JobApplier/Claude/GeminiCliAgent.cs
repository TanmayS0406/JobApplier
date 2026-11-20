using System.Runtime.CompilerServices;
using System.Text;

namespace JobApplier.Claude;

/// <summary>
/// Google's Gemini CLI as an agent, signed in with the user's own Google account (or an API key in the environment). It plays
/// the same role as <see cref="ClaudeCodeAgent"/> and produces the same events, so a job can run on either provider.
/// <para>
/// Differences from the Claude side that this class has to bridge:
/// </para>
/// <list type="bullet">
/// <item>There is no system-prompt flag, so the instructions are placed at the top of the prompt.</item>
/// <item>There is no <c>--json-schema</c>: the schema is put in the prompt and the JSON object is taken from the answer
/// (<see cref="JsonExtract"/>). An answer without a usable object fails the run instead of guessing.</item>
/// <item>MCP servers and the tool allow-list come from a settings file kept in the private folder
/// (<see cref="GeminiSettings"/>) rather than from command-line flags.</item>
/// <item>There is no <c>--restricted</c>: confinement is the allow-list (one read-only file tool), non-interactive mode denying
/// anything that would need a confirmation, and the browser tools being the only trusted servers.</item>
/// </list>
/// </summary>
public sealed class GeminiCliAgent : CliAgent
{
    /// <summary>Environment variable that makes the CLI load the generated settings file as its system settings.</summary>
    public const string SettingsVariable = "GEMINI_CLI_SYSTEM_SETTINGS_PATH";

    /// <summary>Environment variable that moves the CLI's whole <c>.gemini</c> folder (settings, state) somewhere else.</summary>
    public const string HomeVariable = "GEMINI_CLI_HOME";

    public GeminiCliAgent(ClaudeAgentSpec spec) : base(spec) { }

    protected override async IAsyncEnumerable<ClaudeEvent> ExecuteAsync(
        string prompt, string? schema, ClaudeAgentSession? session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cliPath = GeminiCli.Locate(Spec.ClaudePath)
            ?? throw new ClaudeAgentException("Gemini CLI not found. Install it with 'npm install -g @google/gemini-cli', or set its path in Settings.");

        Directory.CreateDirectory(Spec.PrivateFolder);
        var settings = GeminiSettings.Build(Spec).ToJsonString();
        var info = ClaudeCli.CreateStartInfo(cliPath, BuildArguments(), Spec.WorkingDirectory);
        GeminiCli.ApplyStoredApiKey(info.Environment);   // a key saved from Settings authenticates the run without the user setting a variable

        if (GeminiCli.HasApiKey(info.Environment))
        {
            // The CLI ignores a "system settings" file that is not in a folder it considers admin-owned ("Skipping system settings file ...
            // is insecure" - always the case for a folder of ours on Windows), and with it the browser tool and the tool restrictions. User
            // settings are always read, and with an API key no Google login has to be found in the real home, so the run gets its own home.
            var home = Path.Combine(Spec.PrivateFolder, "gemini-home");
            Directory.CreateDirectory(Path.Combine(home, ".gemini"));
            File.WriteAllText(Path.Combine(home, ".gemini", "settings.json"), settings, new UTF8Encoding(false));
            info.Environment[HomeVariable] = home;
        }
        else
        {
            var settingsFile = Path.Combine(Spec.PrivateFolder, $"{Safe(Spec.Name)}-gemini-settings.json");
            File.WriteAllText(settingsFile, settings, new UTF8Encoding(false));
            info.Environment[SettingsVariable] = settingsFile;
        }
        using var process = ClaudeCli.Start(info);
        using var kill = cancellationToken.Register(() => ClaudeCli.KillTree(process));

        // The prompt goes through stdin: no command-line length or quoting limits. The short -p text only tells the CLI to act on it.
        await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            await stdin.WriteAsync(BuildPrompt(Spec.Instructions, prompt, schema).AsMemory(), cancellationToken);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var parser = new GeminiStreamParser();
        ResultEvent? result = null;

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
        var stderr = (await stderrTask).Trim();

        if (result is { IsError: true })
            throw Describe(result.Text ?? parser.LastError ?? stderr);
        if (result == null)
            throw Describe(FirstNonEmpty(stderr, parser.LastError) ?? $"Gemini CLI exited with code {process.ExitCode} without a result.");
        GeminiHealth.GoogleSignInRejected = false;   // it ran: whatever sign-in is used works
        if (schema != null && result.StructuredJson == null)
            throw new ClaudeAgentException(
                "Gemini did not return the structured result that was asked for. If this was the Apply step, check the employer's site before retrying: the form may already have been submitted. " +
                "What it said: " + Clip(result.Text, 300));
    }

    /// <summary>The instructions, the task and, for a typed result, the schema and the rule that the answer is that JSON and nothing else.</summary>
    internal static string BuildPrompt(string instructions, string task, string? schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine(instructions.TrimEnd());
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

    private List<string> BuildArguments()
    {
        var args = new List<string>
        {
            "-p", "Carry out the task described above, then give the result in the format it asks for.",
            "--output-format", "stream-json"
        };
        if (!string.IsNullOrWhiteSpace(Spec.Model)) { args.Add("--model"); args.Add(Spec.Model.Trim()); }
        return args;
    }

    private static string Safe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "(nothing)" : text.Length <= max ? text : text[..max] + "...";

    internal static ClaudeAgentException Describe(string error)
    {
        var cleaned = GeminiErrors.Clean(error);
        if (GeminiErrors.IsRetiredClient(cleaned))
        {
            GeminiHealth.GoogleSignInRejected = true;
            return new ClaudeAgentException(GeminiErrors.RetiredClientMessage, isAuthProblem: true);
        }
        return AgentErrors.Describe(cleaned, "Open Settings and press 'Sign in with Google', or paste a Gemini API key.");
    }
}
