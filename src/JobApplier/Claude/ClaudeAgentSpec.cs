using System.Text.Json;
using System.Text.Json.Nodes;
using JobApplier.Infrastructure;

namespace JobApplier.Claude;

/// <summary>Everything that defines one Claude-backed agent: who it is, what it may touch, and where it works.</summary>
public sealed class ClaudeAgentSpec
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";

    /// <summary>System prompt appended to Claude Code's own (passed as a file, so length and characters are unrestricted).</summary>
    public required string Instructions { get; init; }

    /// <summary>Working directory. With <c>--restricted</c> the agent's file tools cannot leave it.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Folder for the generated system prompt / MCP config. Must NOT be inside <see cref="WorkingDirectory"/>.</summary>
    public required string PrivateFolder { get; init; }

    public string ClaudePath { get; init; } = "";
    public string Model { get; init; } = "";
    public string Effort { get; init; } = "";

    /// <summary>Claude Code built-in tools to enable, e.g. <c>Read</c>. Empty = none (no Bash, no Edit, no web).</summary>
    public IReadOnlyList<string> BuiltInTools { get; init; } = Array.Empty<string>();

    /// <summary>MCP servers by name; the value is the server definition in Claude's <c>--mcp-config</c> format.</summary>
    public IReadOnlyDictionary<string, JsonObject> McpServers { get; init; } = new Dictionary<string, JsonObject>();

    /// <summary>Keep the Claude session on disk so a later run can <c>--resume</c> it. Off by default to avoid clutter.</summary>
    public bool PersistSession { get; init; }

    /// <summary>Called for every decoded event while the agent runs (tool calls, text, result) - drives the job log.</summary>
    public Action<ClaudeEvent>? OnEvent { get; init; }
}

/// <summary>A failure of an agent run (either provider): the CLI is missing or signed out, a limit was hit, or the run went wrong.</summary>
public sealed class ClaudeAgentException : Exception
{
    public ClaudeAgentException(string message, bool isAuthProblem = false, bool isUsageLimit = false, DateTime? retryAfterUtc = null) : base(message)
    {
        IsAuthProblem = isAuthProblem;
        IsUsageLimit = isUsageLimit;
        RetryAfterUtc = retryAfterUtc;
    }

    /// <summary>True when the CLI is not signed in (the UI then points at the Sign-in button).</summary>
    public bool IsAuthProblem { get; }

    /// <summary>True when a token, quota or rate limit stopped the run. Waiting fixes it, so the job can be retried later.</summary>
    public bool IsUsageLimit { get; }

    /// <summary>When the limit is expected to reset, if the error said so.</summary>
    public DateTime? RetryAfterUtc { get; }
}

/// <summary>Turns the text a CLI failed with into the right <see cref="ClaudeAgentException"/>, whichever provider produced it.</summary>
public static class AgentErrors
{
    private static readonly string[] AuthSignals =
    {
        "not logged in", "/login", "api key", "authenticat", "oauth", "unauthorized",
        "please set an auth method", "gemini_api_key", "failed to login", "login with google", "not signed in"
    };

    /// <param name="signInHint">What to tell the user to do when the problem is sign-in, e.g. "Open Settings and press 'Sign in with Claude'."</param>
    public static ClaudeAgentException Describe(string error, string signInHint, DateTime? nowUtc = null)
    {
        error ??= "";
        if (UsageLimit.IsLimit(error))
            return new ClaudeAgentException(error, isUsageLimit: true, retryAfterUtc: UsageLimit.ResetTime(error, nowUtc ?? DateTime.UtcNow));

        var lower = error.ToLowerInvariant();
        var auth = AuthSignals.Any(lower.Contains);
        return new ClaudeAgentException(auth ? error + " " + signInHint : error, auth);
    }
}

/// <summary>
/// Adapts the schema that Microsoft.Extensions.AI generates from a C# type to what <c>claude --json-schema</c> accepts:
/// the CLI's validator does not know the draft-2020-12 meta-schema, so the <c>$schema</c> keyword is dropped, and structured
/// output wants closed objects, so <c>additionalProperties: false</c> is added where it is missing.
/// </summary>
public static class SchemaSanitizer
{
    public static string Sanitize(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        if (node is JsonObject root) root.Remove("$schema");
        Visit(node);
        return node.ToJsonString();
    }

    private static void Visit(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((string?)obj["type"] == "object" && !obj.ContainsKey("additionalProperties"))
                    obj["additionalProperties"] = false;
                foreach (var child in obj.Select(p => p.Value).ToList()) Visit(child);
                break;
            case JsonArray array:
                foreach (var child in array) Visit(child);
                break;
        }
    }
}
