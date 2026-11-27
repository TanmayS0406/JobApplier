namespace JobApplier.Models;

/// <summary>A model field of the Settings page: its form name, what is saved now, and the models to choose from (the empty value is the CLI's own default).</summary>
public sealed record ModelPicker(string Name, string Value, IReadOnlyList<(string Value, string Label)> Choices);

/// <summary>
/// The models offered in Settings. Neither CLI can list the models of an account (Claude Code has no such command, and Copilot's <c>--model</c>
/// has no list), so these are the ones known to work, and the dropdown's last choice lets any other name be typed.
/// </summary>
public static class ModelChoices
{
    /// <summary>Claude Code takes an alias (always the newest of that family) or a full model id.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Claude = new (string, string)[]
    {
        ("", "Claude Code default"),
        ("sonnet", "Sonnet (latest)"), ("opus", "Opus (latest)"), ("haiku", "Haiku (latest, fastest)"), ("fable", "Fable (latest)"),
        ("claude-fable-5-2", "Fable 5.2"), ("claude-opus-5-5", "Opus 5.5"), ("claude-sonnet-5-5", "Sonnet 5.5"), ("claude-haiku-5-5", "Haiku 5.5")
    };

    /// <summary>What a Copilot plan offers varies; <c>auto</c> always works.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Copilot = new (string, string)[]
    {
        ("", "Copilot CLI default"), ("auto", "Auto (Copilot picks)"),
        ("claude-sonnet-4.5", "Claude Sonnet 4.5"), ("claude-opus-4.5", "Claude Opus 4.5"), ("gpt-5", "GPT-5"), ("gpt-5-mini", "GPT-5 mini"), ("gpt-5.6-sol", "GPT-5.6 Sol")
    };
}
