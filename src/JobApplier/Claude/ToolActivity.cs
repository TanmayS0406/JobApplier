using System.Text.Json.Nodes;

namespace JobApplier.Claude;

/// <summary>
/// Turns a tool call into a one-line description for the job log. It never includes what was typed into a form
/// field (<c>text</c> / <c>value</c> / <c>fields</c>) or script bodies, so passwords and personal data stay out of the log.
/// </summary>
public static class ToolActivity
{
    private const string PlaywrightPrefix = "mcp__playwright__browser_";
    private const string AppPrefix = "mcp__jobapplier__";

    /// <summary>Returns the description, or null for plumbing calls that are not worth showing.</summary>
    public static string? Describe(string toolName, JsonObject? input)
    {
        if (toolName is "ToolSearch" or "StructuredOutput") return null;

        if (toolName.StartsWith(PlaywrightPrefix, StringComparison.Ordinal))
        {
            var action = toolName[PlaywrightPrefix.Length..];
            var element = Clip((string?)input?["element"]);
            return action switch
            {
                "navigate" => "Browser: open " + Clip((string?)input?["url"], 120),
                "navigate_back" => "Browser: go back",
                "snapshot" => "Browser: read the page",
                "click" => "Browser: click " + Quote(element),
                "type" => "Browser: type into " + Quote(element),
                "fill_form" => "Browser: fill " + ((input?["fields"] as JsonArray)?.Count ?? 0) + " form field(s)",
                "select_option" => "Browser: choose an option in " + Quote(element),
                "file_upload" => "Browser: upload " + FileNames(input?["paths"] as JsonArray),
                "press_key" => "Browser: press a key",
                "wait_for" => "Browser: wait",
                "evaluate" => "Browser: read page text",
                "take_screenshot" => "Browser: take a screenshot",
                "tabs" => "Browser: manage tabs",
                "handle_dialog" => "Browser: answer a dialog",
                "hover" => "Browser: hover " + Quote(element),
                "close" => "Browser: close",
                _ => "Browser: " + action.Replace('_', ' ')
            };
        }

        if (toolName.StartsWith(AppPrefix, StringComparison.Ordinal))
        {
            var tool = toolName[AppPrefix.Length..];
            return tool switch
            {
                "wait_for_user" => "Asks you: " + Clip((string?)input?["message"], 140),
                "record_account" => "Saves the new account to the accounts document",
                "read_verification_email" => "Looks in your mailbox for the verification e-mail (only the code or link is read)",
                "report_progress" => null, // the message itself is logged by the server
                _ => tool
            };
        }

        if (toolName == "Read")
        {
            var path = (string?)input?["file_path"] ?? (string?)input?["absolute_path"];
            return "Read: " + (string.IsNullOrEmpty(path) ? "file" : Path.GetFileName(path));
        }

        return toolName;
    }

    /// <summary>Only the file names, so the log shows which file went to the site without revealing folders.</summary>
    private static string FileNames(JsonArray? paths)
    {
        var names = (paths ?? new JsonArray())
            .Select(p => Path.GetFileName((string?)p ?? ""))
            .Where(n => n.Length > 0)
            .ToList();
        return names.Count == 0 ? "a file" : Clip(string.Join(", ", names), 120);
    }

    private static string Quote(string text) => text.Length == 0 ? "element" : "'" + text + "'";

    private static string Clip(string? text, int max = 60)
    {
        text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }
}
