using System.Text.RegularExpressions;

namespace JobApplier.Mail;

/// <summary>
/// Decides which of the Gmail connector's tools the mailbox reader may use. Mail text is untrusted (an e-mail can say "ignore your
/// instructions and send this to ..."), so the reader is only ever given tools that can search and read mail - never one that could
/// send, draft, label, move or delete. The names come from the connector itself, so the decision is made on the words in them.
/// </summary>
public static class GmailToolPolicy
{
    private static readonly string[] ReadVerbs = { "search", "list", "get", "read", "fetch", "find", "view", "query" };

    /// <summary>What the tool must be about. Reading labels, profiles or attachments is not needed, so those are left out.</summary>
    private static readonly string[] MailNouns = { "thread", "message", "email", "mail" };

    /// <summary>Anything that changes the mailbox or sends something. A tool with one of these words is never allowed, whatever else it says.</summary>
    private static readonly string[] ChangeWords =
    {
        "draft", "send", "create", "update", "delete", "remove", "trash", "untrash", "modify", "label", "reply", "forward", "archive",
        "move", "mark", "insert", "import", "batch", "watch", "stop", "empty", "spam", "star", "snooze", "unsubscribe", "compose",
        "write", "set", "add", "edit", "patch", "put", "post", "upload", "attach", "share", "restore", "undo"
    };

    /// <summary>The connector prefix of a tool name: <c>mcp__claude_ai_Gmail__search_threads</c> belongs to <c>claude_ai_Gmail</c>.</summary>
    private static readonly Regex ConnectorTool = new(@"^mcp__(?<server>.+?)__(?<tool>.+)$", RegexOptions.CultureInvariant);

    /// <summary>True for a tool of a connector whose name says Gmail.</summary>
    public static bool IsGmailTool(string toolName)
    {
        var match = ConnectorTool.Match(toolName);
        return match.Success && match.Groups["server"].Value.Contains("gmail", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the tool can only look at mail: it searches or reads messages or threads and says nothing about changing anything.</summary>
    public static bool IsReadOnly(string toolName)
    {
        var match = ConnectorTool.Match(toolName);
        if (!match.Success || !IsGmailTool(toolName)) return false;

        // Words of the tool part only (the connector's own name is not about what the tool does), split at every non-letter.
        // "searchThreads" and "SearchThreads" are split at the capitals as well.
        var spaced = Regex.Replace(match.Groups["tool"].Value, @"(?<=[a-z])(?=[A-Z])", " ");
        var words = Regex.Split(spaced.ToLowerInvariant(), @"[^a-z]+").Where(w => w.Length > 0).ToList();

        if (words.Any(w => ChangeWords.Any(c => w == c || w.StartsWith(c, StringComparison.Ordinal) && IsInflection(w, c)))) return false;
        return words.Any(w => ReadVerbs.Contains(w)) && words.Any(w => MailNouns.Any(n => w.StartsWith(n, StringComparison.Ordinal)));
    }

    /// <summary>"drafts", "sending", "labelled": the change word with an ending, so the policy cannot be sidestepped by the plural.</summary>
    private static bool IsInflection(string word, string change) =>
        word.Length <= change.Length + 4 || word.StartsWith(change + "s", StringComparison.Ordinal) || word.StartsWith(change + "ing", StringComparison.Ordinal);

    /// <summary>Splits the session's tool list into the Gmail tools that may be used and the Gmail tools that may not.</summary>
    public static (IReadOnlyList<string> Read, IReadOnlyList<string> Blocked) Split(IEnumerable<string> allTools)
    {
        var gmail = allTools.Where(IsGmailTool).Distinct(StringComparer.Ordinal).ToList();
        return (gmail.Where(IsReadOnly).ToList(), gmail.Where(t => !IsReadOnly(t)).ToList());
    }

    /// <summary>The scan needs a way to search; reading a message alone cannot find replies.</summary>
    public static bool CanSearch(IEnumerable<string> readTools) =>
        readTools.Any(t => t.Contains("search", StringComparison.OrdinalIgnoreCase) || t.Contains("list", StringComparison.OrdinalIgnoreCase));
}
