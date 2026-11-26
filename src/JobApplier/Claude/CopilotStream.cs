using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobApplier.Claude;

/// <summary>An advisory from the CLI worth showing in the job log, for example that a tool server could not start.</summary>
public sealed record NoticeEvent(string Text) : ClaudeEvent;

/// <summary>
/// Decodes <c>copilot -p --output-format json</c>: one JSON object per line, each <c>{ "type", "data", "id", "timestamp", ... }</c>. The
/// events used are <c>session.start</c>, <c>assistant.message</c>, <c>tool.execution_start</c> / <c>tool.execution_complete</c>,
/// <c>assistant.usage</c>, <c>session.warning</c> / <c>session.error</c>, and the last line, <c>{ "type": "result", "exitCode", ... }</c>.
/// They become the same <see cref="ClaudeEvent"/>s as on the Claude side, so the rest of the application (job log, password detection,
/// typed results) does not care which provider ran.
/// <para>
/// The text the assistant writes before it calls a tool is reported as a <see cref="TextEvent"/> when the tool starts. The text after the
/// last tool call is the final answer; a typed result is the JSON object in it (<see cref="JsonExtract"/>).
/// </para>
/// </summary>
public sealed class CopilotStreamParser
{
    private readonly StringBuilder _segment = new();
    private readonly Dictionary<string, string> _toolDescriptions = new();
    private readonly List<string> _denials = new();
    private string? _sessionId;
    private long _inputTokens;
    private long _outputTokens;

    /// <summary>The last error the CLI reported as an event, for a failure message when the result carries none.</summary>
    public string? LastError { get; private set; }

    /// <summary>The kind of that error: <c>authentication</c>, <c>authorization</c>, <c>policy</c>, <c>quota</c>, <c>rate_limit</c>, <c>context_limit</c>, <c>query</c>, ...</summary>
    public string? LastErrorType { get; private set; }

    public IReadOnlyList<ClaudeEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Array.Empty<ClaudeEvent>();

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return Array.Empty<ClaudeEvent>(); }
        if (node is not JsonObject message) return Array.Empty<ClaudeEvent>();

        var data = message["data"] as JsonObject;
        switch (Str(message["type"]))
        {
            case "session.start":
                _sessionId = Str(data?["sessionId"]);
                return new ClaudeEvent[] { new InitEvent(_sessionId, Array.Empty<(string, string)>()) };

            case "session.warning":
                // Only a tool server that failed to start matters to the user; telemetry and the like are the CLI's own business.
                return Str(data?["warningType"]) == "mcp" && Str(data?["message"]) is { Length: > 0 } warning
                    ? new ClaudeEvent[] { new NoticeEvent(warning) }
                    : Array.Empty<ClaudeEvent>();

            case "session.error":
                LastError = Str(data?["message"]) ?? LastError;
                LastErrorType = Str(data?["errorType"]) ?? LastErrorType;
                return Array.Empty<ClaudeEvent>();

            case "assistant.message":
                return AssistantMessage(data);

            case "tool.execution_start":
                return ToolStart(data);

            case "tool.execution_complete":
                ToolComplete(data);
                return Array.Empty<ClaudeEvent>();

            case "assistant.usage":
                _inputTokens += Long(data?["inputTokens"]);
                _outputTokens += Long(data?["outputTokens"]);
                return Array.Empty<ClaudeEvent>();

            case "result":
                return Result(message);

            default:
                return Array.Empty<ClaudeEvent>();
        }
    }

    /// <summary>Text the CLI wrote after its last event, for a run that ended without a result event.</summary>
    public string TakeSegment()
    {
        var text = _segment.ToString().Trim();
        _segment.Clear();
        return text;
    }

    private IReadOnlyList<ClaudeEvent> AssistantMessage(JsonObject? data)
    {
        var content = Str(data?["content"]) ?? "";
        if (content.Length > 0)
        {
            if (_segment.Length > 0) _segment.Append('\n');
            _segment.Append(content);
        }

        // A message that asks for tools is not the answer: what it said is reported now and the answer starts again after the tools.
        if (data?["toolRequests"] is JsonArray { Count: > 0 } && TakeSegment() is { Length: > 0 } said)
            return new ClaudeEvent[] { new TextEvent(said) };
        return Array.Empty<ClaudeEvent>();
    }

    private IReadOnlyList<ClaudeEvent> ToolStart(JsonObject? data)
    {
        var events = new List<ClaudeEvent>();
        if (TakeSegment() is { Length: > 0 } said) events.Add(new TextEvent(said));

        var (name, input) = CopilotToolNames.Normalize(
            Str(data?["toolName"]) ?? "tool", Str(data?["mcpServerName"]), Str(data?["mcpToolName"]), data?["arguments"] as JsonObject);
        events.Add(new ToolUseEvent(name, input));

        if (Str(data?["toolCallId"]) is { } id) _toolDescriptions[id] = ToolActivity.Describe(name, input) ?? name;
        return events;
    }

    private void ToolComplete(JsonObject? data)
    {
        if (data?["success"] is JsonValue ok && ok.TryGetValue<bool>(out var success) && !success)
        {
            var error = data["error"] as JsonObject;
            var text = (Str(error?["code"]) + " " + Str(error?["message"])).ToLowerInvariant();
            // A tool the run's rules do not allow is refused rather than asked about: say so in the log instead of leaving a silent gap.
            if (text.Contains("denied") || text.Contains("not allowed") || text.Contains("permission") || text.Contains("rejected"))
            {
                var id = Str(data["toolCallId"]);
                _denials.Add(id != null && _toolDescriptions.TryGetValue(id, out var what) ? what : "a tool call");
            }
        }
    }

    private IReadOnlyList<ClaudeEvent> Result(JsonObject message)
    {
        var exitCode = message["exitCode"] is JsonValue code && code.TryGetValue<int>(out var n) ? n : 0;
        var isError = exitCode != 0;
        var final = TakeSegment();
        var json = isError ? null : JsonExtract.FirstObject(final);

        var events = new List<ClaudeEvent>();
        if (!isError && json == null && final.Length > 0) events.Add(new TextEvent(final));   // a plain answer is worth logging
        events.Add(new ResultEvent(isError, isError ? LastError : final, json, Str(message["sessionId"]) ?? _sessionId, 0, _inputTokens, _outputTokens, _denials.ToList()));
        return events;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}

/// <summary>
/// Copilot reports an MCP tool with its server and tool name (<c>mcpServerName</c>, <c>mcpToolName</c>) and has its own file viewer,
/// <c>view</c>. The rest of the application knows the Claude spelling (<c>mcp__playwright__browser_click</c>, <c>Read</c>), so names are
/// mapped to it here.
/// </summary>
public static class CopilotToolNames
{
    private static readonly string[] AppTools = { "wait_for_user", "record_account", "read_verification_email", "report_progress" };

    public static (string Name, JsonObject? Input) Normalize(string toolName, string? mcpServer, string? mcpTool, JsonObject? input)
    {
        toolName = toolName.Trim();

        if (!string.IsNullOrWhiteSpace(mcpServer))
        {
            var tool = string.IsNullOrWhiteSpace(mcpTool) ? toolName : mcpTool.Trim();
            // A server's name may be repeated in front of the tool ("playwright-browser_click"): the tool itself is what follows.
            foreach (var separator in new[] { "-", "_", "__", "." })
            {
                if (tool.StartsWith(mcpServer + separator, StringComparison.OrdinalIgnoreCase)) { tool = tool[(mcpServer.Length + separator.Length)..]; break; }
            }
            return ($"mcp__{mcpServer.Trim()}__{tool}", input);
        }

        // No server given: recognise ours by the tool's own name.
        var browser = toolName.LastIndexOf("browser_", StringComparison.Ordinal);
        if (browser >= 0) return ("mcp__playwright__" + toolName[browser..], input);

        var app = AppTools.FirstOrDefault(t => toolName.EndsWith(t, StringComparison.Ordinal));
        if (app != null) return ("mcp__jobapplier__" + app, input);

        if (toolName is "view" or "read" or "read_file" or "View")
        {
            // ToolActivity reads "file_path"; Copilot's file viewer names it "path".
            if (input != null && input["file_path"] == null && input["path"] is { } path)
                input = new JsonObject(input.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))) { ["file_path"] = path.DeepClone() };
            return ("Read", input);
        }

        return (toolName, input);
    }
}

/// <summary>
/// Builds the MCP configuration the Copilot CLI is started with (<c>--additional-mcp-config @file</c>). It lives in the job's private
/// folder, so it can hold the app's bearer token without the agent being able to read it.
/// </summary>
public static class CopilotMcpConfig
{
    public static JsonObject Build(ClaudeAgentSpec spec)
    {
        var servers = new JsonObject();
        foreach (var (name, definition) in spec.McpServers) servers[name] = Convert(definition);
        return new JsonObject { ["mcpServers"] = servers };
    }

    /// <summary>Claude's MCP definition (<c>type: http, url, headers</c> or <c>command, args, env</c>) in Copilot's spelling, with every tool of the server exposed.</summary>
    internal static JsonObject Convert(JsonObject claude)
    {
        var result = new JsonObject();
        if ((string?)claude["type"] == "http" || claude["url"] != null)
        {
            result["type"] = "http";
            result["url"] = claude["url"]?.DeepClone();
            if (claude["headers"] != null) result["headers"] = claude["headers"]!.DeepClone();
        }
        else
        {
            result["type"] = "local";
            result["command"] = claude["command"]?.DeepClone();
            if (claude["args"] != null) result["args"] = claude["args"]!.DeepClone();
            if (claude["env"] != null) result["env"] = claude["env"]!.DeepClone();
        }
        result["tools"] = new JsonArray("*");
        return result;
    }
}

/// <summary>What the Copilot CLI prints besides its answer, and how its failures are told apart.</summary>
public static class CopilotErrors
{
    public const string SignInHint = "Open Settings and press 'Sign in with GitHub' in the GitHub Copilot card.";

    /// <summary>Error kinds (<c>errorType</c> of a <c>session.error</c>) that mean a limit was reached: waiting fixes them.</summary>
    public static bool IsLimitType(string? errorType) => errorType is "rate_limit" or "quota";

    /// <summary>Error kinds that mean the account may not use this: not signed in, or not allowed (a licence or an organisation policy).</summary>
    public static bool IsAccessType(string? errorType) => errorType is "authentication" or "authorization" or "policy";

    /// <summary>The CLI's message without the usual "To resolve this, try the following:" list, which names slash commands that do not exist here.</summary>
    public static string Clean(string? error)
    {
        var lines = (error ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var advice = lines.FindIndex(l => l.StartsWith("To resolve this", StringComparison.OrdinalIgnoreCase));
        if (advice > 0) lines = lines.Take(advice).ToList();
        var text = string.Join(" ", lines);
        return text.Length > 0 ? text : (error ?? "").Trim();
    }
}
