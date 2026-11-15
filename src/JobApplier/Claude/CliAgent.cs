using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace JobApplier.Claude;

/// <summary>Conversation state of a CLI agent: the provider's session id, used to resume a run.</summary>
public sealed class ClaudeAgentSession : AgentSession
{
    public string? ClaudeSessionId { get; set; }
}

/// <summary>
/// A Microsoft Agent Framework <see cref="AIAgent"/> whose model and tool loop is a command-line coding agent signed in with the
/// user's own account (no API key held by this application). Everything that does not depend on the provider lives here:
/// sessions, the adapters for <c>RunAsync&lt;T&gt;</c> and streaming, and turning the response format into a JSON schema.
/// A subclass only knows how to start its CLI and decode its output (<see cref="ClaudeCodeAgent"/>, <see cref="GeminiCliAgent"/>).
/// </summary>
public abstract class CliAgent : AIAgent
{
    protected CliAgent(ClaudeAgentSpec spec) => Spec = spec;

    protected ClaudeAgentSpec Spec { get; }

    public override string? Name => Spec.Name;

    public override string? Description => Spec.Description;

    /// <summary>Runs the CLI once and yields what it reports. A failed run throws <see cref="ClaudeAgentException"/> after the last event.</summary>
    /// <param name="prompt">The user part of the prompt.</param>
    /// <param name="jsonSchema">The result schema, already adapted for the CLI, or null for a free-text answer.</param>
    protected abstract IAsyncEnumerable<ClaudeEvent> ExecuteAsync(
        string prompt, string? jsonSchema, ClaudeAgentSession? session, CancellationToken cancellationToken);

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new ClaudeAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        var id = (session as ClaudeAgentSession)?.ClaudeSessionId;
        return new(JsonSerializer.SerializeToElement(new { claudeSessionId = id }, jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        var session = new ClaudeAgentSession();
        if (serializedState.ValueKind == JsonValueKind.Object && serializedState.TryGetProperty("claudeSessionId", out var id))
            session.ClaudeSessionId = id.GetString();
        return new(session);
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken = default)
    {
        ResultEvent? result = null;
        var text = new StringBuilder();

        await foreach (var evt in RunEventsAsync(messages, session, options, cancellationToken))
        {
            if (evt is TextEvent t) text.AppendLine(t.Text);
            else if (evt is ResultEvent r) result = r;
        }

        // With a result schema the final answer is the structured output; otherwise it is the last text the agent wrote.
        var answer = result?.StructuredJson ?? result?.Text ?? text.ToString().Trim();
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, answer))
        {
            AgentId = Id,
            ResponseId = result?.SessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Usage = result == null ? null : new UsageDetails { InputTokenCount = result.InputTokens, OutputTokenCount = result.OutputTokens }
        };
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in RunEventsAsync(messages, session, options, cancellationToken))
        {
            switch (evt)
            {
                case TextEvent t:
                    yield return new AgentResponseUpdate(ChatRole.Assistant, t.Text) { AuthorName = Name, AgentId = Id };
                    break;
                case ResultEvent { StructuredJson: { } json }:
                    yield return new AgentResponseUpdate(ChatRole.Assistant, json) { AuthorName = Name, AgentId = Id };
                    break;
            }
        }
    }

    private IAsyncEnumerable<ClaudeEvent> RunEventsAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
    {
        var prompt = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        var schema = options?.ResponseFormat is ChatResponseFormatJson { Schema: { } element } ? SchemaSanitizer.Sanitize(element) : null;
        return ExecuteAsync(prompt, schema, session as ClaudeAgentSession, cancellationToken);
    }
}
