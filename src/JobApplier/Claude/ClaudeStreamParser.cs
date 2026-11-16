using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobApplier.Claude;

/// <summary>One decoded line of <c>claude -p --output-format stream-json</c>.</summary>
public abstract record ClaudeEvent;

/// <param name="McpServers">Every MCP server the session knows, with its status (<c>connected</c>, <c>needs-auth</c>, <c>failed</c>, ...).</param>
/// <param name="Tools">The names of the tools the session has loaded (<c>mcp__server__tool</c> for connector tools). Null when the CLI did not say.</param>
public sealed record InitEvent(string? SessionId, IReadOnlyList<(string Name, string Status)> McpServers, IReadOnlyList<string>? Tools = null) : ClaudeEvent;

public sealed record TextEvent(string Text) : ClaudeEvent;

public sealed record ToolUseEvent(string Name, JsonObject? Input) : ClaudeEvent;

public sealed record ResultEvent(
    bool IsError,
    string? Text,
    string? StructuredJson,
    string? SessionId,
    double CostUsd,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string> Denials) : ClaudeEvent;

public static class ClaudeStreamParser
{
    /// <summary>Decodes one stdout line. Lines that are not stream-json (or not interesting) yield nothing.</summary>
    public static IReadOnlyList<ClaudeEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Array.Empty<ClaudeEvent>();

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return Array.Empty<ClaudeEvent>(); }

        if (node is not JsonObject message) return Array.Empty<ClaudeEvent>();

        switch ((string?)message["type"])
        {
            case "system" when (string?)message["subtype"] == "init":
                var servers = new List<(string, string)>();
                if (message["mcp_servers"] is JsonArray array)
                {
                    foreach (var server in array.OfType<JsonObject>())
                        servers.Add(((string?)server["name"] ?? "", (string?)server["status"] ?? ""));
                }
                var tools = message["tools"] is JsonArray toolArray
                    ? toolArray.Select(t => (string?)t).OfType<string>().ToList()
                    : null;
                return new ClaudeEvent[] { new InitEvent((string?)message["session_id"], servers, tools) };

            case "assistant":
                var events = new List<ClaudeEvent>();
                if (message["message"]?["content"] is JsonArray content)
                {
                    foreach (var block in content.OfType<JsonObject>())
                    {
                        switch ((string?)block["type"])
                        {
                            case "text":
                                var text = (string?)block["text"];
                                if (!string.IsNullOrWhiteSpace(text)) events.Add(new TextEvent(text));
                                break;
                            case "tool_use":
                                events.Add(new ToolUseEvent((string?)block["name"] ?? "tool", block["input"] as JsonObject));
                                break;
                        }
                    }
                }
                return events;

            case "result":
                var denials = new List<string>();
                if (message["permission_denials"] is JsonArray denied)
                {
                    foreach (var d in denied.OfType<JsonObject>())
                        denials.Add(ToolActivity.Describe((string?)d["tool_name"] ?? "tool", d["tool_input"] as JsonObject) ?? ((string?)d["tool_name"] ?? "tool"));
                }

                var structured = message["structured_output"];
                var usage = message["usage"] as JsonObject;
                return new ClaudeEvent[]
                {
                    new ResultEvent(
                        IsError: (bool?)message["is_error"] ?? false,
                        Text: (string?)message["result"],
                        StructuredJson: structured is null ? null : structured.ToJsonString(),
                        SessionId: (string?)message["session_id"],
                        CostUsd: (double?)message["total_cost_usd"] ?? 0,
                        InputTokens: (long?)usage?["input_tokens"] ?? 0,
                        OutputTokens: (long?)usage?["output_tokens"] ?? 0,
                        Denials: denials)
                };

            default:
                return Array.Empty<ClaudeEvent>();
        }
    }
}
