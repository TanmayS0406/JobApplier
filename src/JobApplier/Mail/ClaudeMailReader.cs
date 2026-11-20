using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JobApplier.Claude;
using JobApplier.Infrastructure;
using JobApplier.Services;

namespace JobApplier.Mail;

/// <summary>
/// Reads the user's Gmail through their own Claude account: the Gmail connector that is part of the account (added at
/// claude.ai, signed in to Google there) is used by a headless Claude Code run. JobApplier never sees a Google credential.
/// <para>
/// Two kinds of run are made. A <b>probe</b> starts Claude only long enough to ask which connectors it has and whether they are signed in
/// (that is the one answer that can be trusted; <c>claude mcp list</c> reports connectors as connected that a real session cannot use),
/// then stops it. The account's connectors are loaded a few seconds after Claude starts - its start-up event lists none yet - so the live
/// status is asked for (<c>mcp_status</c>) until Gmail has settled. A <b>scan</b> asks Claude to look through the mailbox; it is given exactly the connector's read-only search and read
/// tools (see <see cref="GmailToolPolicy"/>), no file, shell or web tools, and a permission mode that denies everything else.
/// </para>
/// </summary>
public sealed class ClaudeMailReader : IMailReader
{
    private static readonly TimeSpan ReadyCacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NotReadyCacheFor = TimeSpan.FromSeconds(20);

    // Asks a running session for the current state of its MCP servers. No model is involved, so this costs nothing.
    private const string StatusRequest = "{\"type\":\"control_request\",\"request_id\":\"mcp\",\"request\":{\"subtype\":\"mcp_status\"}}";

    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<ClaudeMailReader> _logger;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private GmailConnectorStatus? _cached;
    private DateTime _cachedAtUtc;

    public ClaudeMailReader(SettingsService settings, AppPaths paths, ILogger<ClaudeMailReader> logger)
    {
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>How long Claude may take to report its connectors.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long Claude may take to connect its claude.ai connectors (a few seconds in practice) before Gmail is taken as not there.</summary>
    public TimeSpan ConnectorWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long one scan of the mailbox may take. The run is stopped after that.</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(12);

    public GmailConnectorStatus? Cached => _cached;

    public async Task<GmailConnectorStatus> GetStatusAsync(bool force, CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _cached != null && DateTime.UtcNow - _cachedAtUtc < (_cached.Ready ? ReadyCacheFor : NotReadyCacheFor))
                return _cached;

            _cached = await ProbeAsync(cancellationToken);
            _cachedAtUtc = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public string? OpenConnectWindow()
    {
        var cli = ClaudeCli.Locate(_settings.Current.ClaudePath);
        if (cli == null) return "Claude Code CLI not found. Install it from https://claude.com/claude-code, or set its path below.";
        try
        {
            // No first input: Claude loads the account's connectors a few seconds after it starts, and a /mcp submitted at once finds none
            // ("No MCP servers loaded"). The user types /mcp once the prompt is up.
            ClaudeCli.StartInteractive(cli, null);
            _cached = null;   // whatever was known is about to change
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return "Could not open Claude: " + ex.Message;
        }
    }

    // ---- probe ----

    private async Task<GmailConnectorStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var cli = ClaudeCli.Locate(_settings.Current.ClaudePath);
        if (cli == null)
            return GmailConnectorStatus.Of(ConnectorState.CliMissing, "Claude Code CLI not found. Install it from https://claude.com/claude-code, or set its path below.");

        Directory.CreateDirectory(_paths.MailWorkspace);
        try
        {
            var init = await ReadConnectorsAsync(cli, cancellationToken);
            return init == null
                ? GmailConnectorStatus.Of(ConnectorState.Failed, "Claude Code ended without saying which connectors it has. Is it signed in? Use 'Sign in with Claude' above.")
                : Evaluate(init);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GmailConnectorStatus.Of(ConnectorState.Failed, $"Claude Code did not start within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            return GmailConnectorStatus.Of(ConnectorState.Failed, "Could not start Claude Code: " + ex.Message);
        }
    }

    /// <summary>What the connectors look like at one moment, as the last <c>mcp_status</c> answer of a session reported them.</summary>
    private sealed class ConnectorWatch
    {
        public volatile InitEvent? Latest;
    }

    private async Task<InitEvent?> ReadConnectorsAsync(string cli, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-p", "--output-format", "stream-json", "--verbose", "--input-format", "stream-json",
            "--no-session-persistence", "--disable-slash-commands", "--tools", "", "--permission-mode", "dontAsk"
        };
        using var process = ClaudeCli.Start(ClaudeCli.CreateStartInfo(cli, arguments, _paths.MailWorkspace));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var kill = timeout.Token.Register(() => ClaudeCli.KillTree(process));

        try
        {
            // The input stays open on purpose: nothing but status questions is ever sent, so no model runs.
            var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            var watch = new ConnectorWatch();
            var reader = ReadSessionAsync(process, watch, null, timeout.Token);
            return await AwaitConnectorsAsync(stdin, watch, reader, timeout.Token);
        }
        finally
        {
            ClaudeCli.KillTree(process);
        }
    }

    /// <summary>Reads a session's output until it ends: remembers the connector status answers and, for a scan, the result.</summary>
    private static async Task ReadSessionAsync(System.Diagnostics.Process process, ConnectorWatch watch, Action<ResultEvent>? onResult, CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
            {
                if (ParseMcpStatus(line) is { } connectors) watch.Latest = connectors;
                foreach (var evt in ClaudeStreamParser.Parse(line))
                    if (evt is ResultEvent result) onResult?.Invoke(result);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // the run was stopped; whoever started it knows why
        }
    }

    /// <summary>
    /// Asks the session for its connectors until Gmail has settled (connected with its tools, signed out, or failed) or <see cref="ConnectorWait"/> is over.
    /// Returns what was last reported; null when the session never answered (it is then up to the caller's timeout).
    /// </summary>
    private async Task<InitEvent?> AwaitConnectorsAsync(StreamWriter stdin, ConnectorWatch watch, Task reader, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ConnectorWait;
        while (true)
        {
            await stdin.WriteLineAsync(StatusRequest.AsMemory(), cancellationToken);
            await Task.Delay(300, cancellationToken);

            var latest = watch.Latest;
            if (latest != null && (Settled(latest) || DateTime.UtcNow >= deadline)) return latest;
            if (reader.IsCompleted) return latest;
        }
    }

    private static bool IsGmailServer((string Name, string Status) s) => s.Name.Contains("gmail", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gmail is in the list and is no longer connecting: connected with tools to offer, or signed out, or failed.</summary>
    internal static bool Settled(InitEvent connectors)
    {
        var gmail = connectors.McpServers.Where(IsGmailServer).ToList();
        if (gmail.Count == 0) return false;
        if (gmail.Any(s => s.Status.Equals("connected", StringComparison.OrdinalIgnoreCase)))
            return (connectors.Tools ?? Array.Empty<string>()).Any(GmailToolPolicy.IsGmailTool);
        return gmail.All(s => !s.Status.Equals("pending", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The connectors in an <c>mcp_status</c> answer, shaped like the start-up event used to be: each server with its status, and the tools of the
    /// connected ones under the names the model sees (<c>mcp__claude_ai_Gmail__search_threads</c>). Null for any other line.
    /// </summary>
    internal static InitEvent? ParseMcpStatus(string line)
    {
        if (!line.Contains("control_response", StringComparison.Ordinal)) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return null; }

        if (node?["type"]?.ToString() != "control_response" || node["response"]?["response"]?["mcpServers"] is not JsonArray servers) return null;

        var list = new List<(string Name, string Status)>();
        var tools = new List<string>();
        foreach (var server in servers)
        {
            var name = server?["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            var status = server?["status"]?.ToString() ?? "";
            list.Add((name, status));

            if (!status.Equals("connected", StringComparison.OrdinalIgnoreCase) || server?["tools"] is not JsonArray serverTools) continue;
            var prefix = "mcp__" + Regex.Replace(name, "[^A-Za-z0-9_-]", "_") + "__";
            tools.AddRange(serverTools.Select(t => t?["name"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)).Select(n => prefix + n));
        }
        return new InitEvent(null, list, tools);
    }

    /// <summary>What a session's connector list says about the Gmail connector.</summary>
    internal static GmailConnectorStatus Evaluate(InitEvent init)
    {
        var gmail = init.McpServers.Where(s => s.Name.Contains("gmail", StringComparison.OrdinalIgnoreCase)).ToList();
        if (gmail.Count == 0)
            return GmailConnectorStatus.Of(ConnectorState.NotAdded,
                "Your Claude account has no Gmail connector yet. Add it at claude.ai (Settings, Connectors) and sign in to Google there.");

        if (!gmail.Any(s => s.Status.Equals("connected", StringComparison.OrdinalIgnoreCase)))
        {
            var status = gmail[0].Status;
            return status.ToLowerInvariant() switch
            {
                "needs-auth" => GmailConnectorStatus.Of(ConnectorState.NeedsAuth,
                    "The Gmail connector is on your Claude account but is not signed in to Google (status: needs-auth). Connect it, then check again."),
                "pending" => GmailConnectorStatus.Of(ConnectorState.Unknown, "Claude was still connecting to Gmail. Check again in a moment."),
                _ => GmailConnectorStatus.Of(ConnectorState.Failed, $"Claude could not use the Gmail connector (status: {status}).")
            };
        }

        var (read, blocked) = GmailToolPolicy.Split(init.Tools ?? Array.Empty<string>());
        if (!GmailToolPolicy.CanSearch(read))
        {
            var names = init.Tools == null ? "none reported" : string.Join(", ", init.Tools.Where(GmailToolPolicy.IsGmailTool).Select(t => t[(t.LastIndexOf("__", StringComparison.Ordinal) + 2)..]));
            return GmailConnectorStatus.Of(ConnectorState.Failed,
                $"The Gmail connector is connected but offers no read-only tool for searching mail that I can use safely (its tools: {names}).");
        }

        return new GmailConnectorStatus(ConnectorState.Ready,
            $"Gmail is connected through your Claude account ({read.Count} read-only tool{(read.Count == 1 ? "" : "s")} will be used; {blocked.Count} that could change mail are blocked).",
            read, blocked);
    }

    // ---- scan ----

    public async Task<IReadOnlyList<ScannedReply>> ScanAsync(MailScanRequest request, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(false, cancellationToken);
        if (!status.Ready) throw new MailReaderException(status.Detail, notReady: true);

        var cli = ClaudeCli.Locate(_settings.Current.ClaudePath)
            ?? throw new MailReaderException("Claude Code CLI not found.", notReady: true);

        Directory.CreateDirectory(_paths.MailWorkspace);
        var info = ClaudeCli.CreateStartInfo(cli, BuildScanArguments(status), _paths.MailWorkspace);
        using var process = ClaudeCli.Start(info);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScanTimeout);
        using var kill = timeout.Token.Register(() => ClaudeCli.KillTree(process));

        ResultEvent? result = null;
        MailReaderException? notReady = null;
        Task<string> stderrTask = Task.FromResult("");
        try
        {
            stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var watch = new ConnectorWatch();
            var reader = ReadSessionAsync(process, watch, r => result = r, timeout.Token);
            var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };

            // Claude connects the account's connectors a few seconds after it starts, and a prompt sent before that is answered with no
            // Gmail tools at all ("nothing found"). So the prompt goes in only once Gmail is connected; if it is not, nothing is sent.
            var connectors = await AwaitConnectorsAsync(stdin, watch, reader, timeout.Token);
            var ready = connectors == null ? null : Evaluate(connectors);
            if (ready is { Ready: true })
            {
                var message = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = MailScanPrompt.Build(request) } });
                await stdin.WriteLineAsync(message.AsMemory(), timeout.Token);
                stdin.Close();   // the session ends once it has answered
            }
            else
            {
                notReady = new MailReaderException(ready?.Detail ?? "Claude Code did not say which connectors it has.", notReady: true);
                ClaudeCli.KillTree(process);
            }
            await reader;
            timeout.Token.ThrowIfCancellationRequested();   // the reader ends quietly when the run is stopped; the time limit must still be reported
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new MailReaderException($"Reading the mailbox took longer than {ScanTimeout.TotalMinutes:0} minutes and was stopped. Try again.");
        }
        finally
        {
            if (!process.HasExited) ClaudeCli.KillTree(process);
        }

        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        if (notReady != null)
        {
            _cached = null;
            throw notReady;
        }

        var stderr = (await stderrTask).Trim();
        if (result is { IsError: true }) throw Describe(result.Text ?? stderr);
        if (result == null) throw Describe(stderr.Length > 0 ? stderr : $"Claude Code exited with code {process.ExitCode} without a result.");

        // Anything Claude tried that was not allowed was refused; only the tool names are logged, never any mail.
        if (result.Denials.Count > 0) _logger.LogWarning("Claude was refused {Count} tool call(s) while reading the mailbox.", result.Denials.Count);

        return ParseReplies(result.StructuredJson, result.Text);
    }

    /// <summary>The arguments of a scan. Everything that is not named is denied (<c>dontAsk</c>), and there are no built-in tools at all.</summary>
    internal List<string> BuildScanArguments(GmailConnectorStatus status)
    {
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--input-format", "stream-json",             // the prompt is sent once Gmail is connected (see ScanAsync)
            "--verbose",
            "--permission-mode", "dontAsk",              // never wait for an approval nobody can see: deny instead
            "--disable-slash-commands",
            "--no-session-persistence",
            "--tools", "",                               // no shell, no files, no web: only the connector's tools below
            "--json-schema", MailScanPrompt.Schema
        };

        var model = _settings.Current.MailModel;
        if (!string.IsNullOrWhiteSpace(model)) { arguments.Add("--model"); arguments.Add(model.Trim()); }

        // Explicitly refuse every Gmail tool that is not read-only, even though dontAsk would deny it anyway.
        if (status.BlockedTools.Count > 0) { arguments.Add("--disallowedTools"); arguments.AddRange(status.BlockedTools); }

        arguments.Add("--allowedTools");   // last: it takes a list
        arguments.AddRange(status.ReadTools);
        return arguments;
    }

    /// <summary>The replies from Claude's answer: the CLI's structured output, or JSON written into its text when that is missing.</summary>
    internal static IReadOnlyList<ScannedReply> ParseReplies(string? structuredJson, string? text)
    {
        foreach (var candidate in new[] { structuredJson, ExtractJsonObject(text) })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var output = JsonSerializer.Deserialize<ScanOutput>(candidate, JsonFile.Options);
                if (output?.Replies != null) return output.Replies.Where(r => r != null).ToList();
            }
            catch (JsonException)
            {
                // try the next candidate
            }
        }
        throw new MailReaderException("Claude's answer could not be read as a list of replies. Try again.");
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static MailReaderException Describe(string error)
    {
        var described = AgentErrors.Describe(error, "Open Settings and press 'Sign in with Claude'.");
        return new MailReaderException(described.Message, notReady: described.IsAuthProblem, isUsageLimit: described.IsUsageLimit, retryAfterUtc: described.RetryAfterUtc);
    }
}
