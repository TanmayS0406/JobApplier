using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobApplier.Claude;

/// <summary>
/// Decodes <c>gemini --output-format stream-json</c>: one JSON object per line with a <c>type</c> of init, message, tool_use,
/// tool_result, error or result. The result is expressed in the same <see cref="ClaudeEvent"/>s as the Claude side, so the
/// rest of the application (job log, password detection, typed results) does not care which provider ran.
/// <para>
/// The assistant's text arrives in small <c>delta</c> chunks. They are collected into one block and reported as a single
/// <see cref="TextEvent"/> when the agent moves on to a tool. Whatever text follows the last tool call is the final answer.
/// </para>
/// </summary>
public sealed class GeminiStreamParser
{
    private readonly StringBuilder _segment = new();
    private string? _sessionId;

    /// <summary>The last error or warning the CLI reported, for a failure message when no result arrives.</summary>
    public string? LastError { get; private set; }

    /// <summary>The assistant text since the last tool call (the answer, once the run is over).</summary>
    public string PendingText => _segment.ToString();

    public IReadOnlyList<ClaudeEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Array.Empty<ClaudeEvent>();

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return Array.Empty<ClaudeEvent>(); }
        if (node is not JsonObject message) return Array.Empty<ClaudeEvent>();

        switch (Str(message["type"]))
        {
            case "init":
                _sessionId = Str(message["session_id"]);
                return new ClaudeEvent[] { new InitEvent(_sessionId, Array.Empty<(string, string)>()) };

            case "message":
                if (string.Equals(Str(message["role"]), "assistant", StringComparison.OrdinalIgnoreCase))
                    _segment.Append(Str(message["content"]));
                return Array.Empty<ClaudeEvent>();

            case "tool_use":
                var events = new List<ClaudeEvent>();
                var said = TakeSegment();
                if (said.Length > 0) events.Add(new TextEvent(said));
                var (name, input) = GeminiToolNames.Normalize(Str(message["tool_name"]) ?? "tool", message["parameters"] as JsonObject);
                events.Add(new ToolUseEvent(name, input));
                return events;

            case "tool_result":
                if (string.Equals(Str(message["status"]), "error", StringComparison.OrdinalIgnoreCase))
                    LastError = Str(message["error"]?["message"]) ?? LastError;
                return Array.Empty<ClaudeEvent>();

            case "error":
                LastError = Str(message["message"]) ?? LastError;
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

    private IReadOnlyList<ClaudeEvent> Result(JsonObject message)
    {
        var status = Str(message["status"]) ?? "success";
        var isError = !status.Equals("success", StringComparison.OrdinalIgnoreCase);
        var final = TakeSegment();
        var stats = message["stats"] as JsonObject;
        long Count(string key) => stats?[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

        var error = Str(message["error"]?["message"]) ?? Str(message["error"]) ?? LastError;
        var json = isError ? null : JsonExtract.FirstObject(final);

        var events = new List<ClaudeEvent>();
        if (!isError && json == null && final.Length > 0) events.Add(new TextEvent(final)); // a plain answer is worth logging
        events.Add(new ResultEvent(isError, isError ? error ?? final : final, json, _sessionId, 0, Count("input_tokens"), Count("output_tokens"), Array.Empty<string>()));
        return events;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>Finds the JSON object in a model's answer, which may be wrapped in a code fence or in a sentence.</summary>
public static class JsonExtract
{
    /// <summary>The first balanced <c>{ ... }</c> in the text that parses as JSON, or null.</summary>
    public static string? FirstObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var end = MatchingBrace(text, start);
            if (end < 0) continue;

            var candidate = text.Substring(start, end - start + 1);
            try
            {
                if (JsonNode.Parse(candidate) is JsonObject) return candidate;
            }
            catch (JsonException)
            {
                // not JSON after all: keep looking
            }
        }
        return null;
    }

    private static int MatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }
}

/// <summary>
/// Gemini names an MCP tool after its server (<c>mcp_playwright_browser_click</c>, <c>playwright__browser_click</c>, ...) and its
/// own file tool <c>read_file</c>. The rest of the application knows the Claude spelling, so names are mapped to it here.
/// </summary>
public static class GeminiToolNames
{
    private static readonly string[] AppTools = { "wait_for_user", "record_account", "report_progress" };

    public static (string Name, JsonObject? Input) Normalize(string name, JsonObject? input)
    {
        name = name.Trim();

        var browser = name.LastIndexOf("browser_", StringComparison.Ordinal);
        if (browser >= 0) return ("mcp__playwright__" + name[browser..], input);

        var app = AppTools.FirstOrDefault(t => name.EndsWith(t, StringComparison.Ordinal));
        if (app != null) return ("mcp__jobapplier__" + app, input);

        if (name is "read_file" or "ReadFile" or "read_many_files")
        {
            // ToolActivity reads "file_path"; Gemini's older spelling is "absolute_path".
            if (input != null && input["file_path"] == null && input["absolute_path"] is { } path)
                input = new JsonObject(input.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))) { ["file_path"] = path.DeepClone() };
            return ("Read", input);
        }

        return (name, input);
    }
}

/// <summary>
/// Builds the settings file Gemini CLI is started with. It is given through <c>GEMINI_CLI_SYSTEM_SETTINGS_PATH</c> and lives in
/// the job's private folder, so it can hold the app's bearer token without the agent being able to read it, and as a system
/// setting it outranks the user's own settings. It names the only MCP servers that may run and the only built-in tool allowed.
/// </summary>
public static class GeminiSettings
{
    /// <summary>The one built-in tool agents may use: it reads files inside the job's workspace and cannot write or run anything.</summary>
    public const string ReadTool = "read_file";

    /// <summary>Tools that could change files, run commands or reach the web outside the browser: refused even if the allow-list is ignored.</summary>
    public static readonly string[] Refused =
    {
        "run_shell_command", "write_file", "replace", "web_fetch", "google_web_search", "save_memory", "write_todos"
    };

    public static JsonObject Build(ClaudeAgentSpec spec)
    {
        var servers = new JsonObject();
        foreach (var (name, definition) in spec.McpServers) servers[name] = Convert(definition);

        return new JsonObject
        {
            ["mcpServers"] = servers,
            ["mcp"] = new JsonObject { ["allowed"] = new JsonArray(spec.McpServers.Keys.Select(k => (JsonNode)k).ToArray()) },
            ["tools"] = new JsonObject
            {
                ["core"] = new JsonArray(ReadTool),
                ["exclude"] = new JsonArray(Refused.Select(t => (JsonNode)t).ToArray())
            }
        };
    }

    /// <summary>Claude's MCP definition (<c>type: http, url</c> or <c>command, args</c>) in Gemini's spelling, trusted so it never asks to confirm.</summary>
    internal static JsonObject Convert(JsonObject claude)
    {
        var result = new JsonObject();
        if ((string?)claude["type"] == "http" || claude["url"] != null)
        {
            result["httpUrl"] = claude["url"]?.DeepClone();
            if (claude["headers"] != null) result["headers"] = claude["headers"]!.DeepClone();
        }
        else
        {
            result["command"] = claude["command"]?.DeepClone();
            if (claude["args"] != null) result["args"] = claude["args"]!.DeepClone();
            if (claude["env"] != null) result["env"] = claude["env"]!.DeepClone();
        }
        result["trust"] = true;
        return result;
    }
}
