using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JobApplier.Domain;
using JobApplier.Mail;
using JobApplier.Services;

namespace JobApplier.Mcp;

/// <summary>
/// The app's own MCP server (Streamable HTTP, JSON responses). The Claude process of every job connects to
/// <c>/mcp/{jobId}</c> with a per-run bearer token and gets the tools that only this app can provide:
/// <c>wait_for_user</c> (hand the browser to the human), <c>record_account</c>, <c>read_verification_email</c> and <c>report_progress</c>.
/// </summary>
public sealed class JobMcpServer
{
    private const string LatestProtocolVersion = "2025-06-18";

    private readonly JobStore _store;
    private readonly HumanHelpBroker _help;
    private readonly AccountService _accounts;
    private readonly ProfileService _profile;
    private readonly SettingsService _settings;
    /// <summary>
    /// How long one wait_for_user call may stay open. It has to be clearly shorter than the Claude CLI's own limit for a tool
    /// call, which in practice is 60 seconds (every job's calls were cut off and repeated at 62 s). A longer wait gets aborted
    /// by the client, which then sees a timeout error instead of "not yet, ask again", re-asks on its own, and can lose the
    /// user's answer to a call nobody is listening to.
    /// </summary>
    public static readonly TimeSpan DefaultWaitSlice = TimeSpan.FromSeconds(45);

    private readonly TimeSpan _waitSlice;

    private readonly VerificationMailService? _verification;

    public JobMcpServer(JobStore store, HumanHelpBroker help, AccountService accounts, ProfileService profile,
                        SettingsService settings, TimeSpan? waitSlice = null, VerificationMailService? verification = null)
    {
        _verification = verification;
        _store = store;
        _help = help;
        _accounts = accounts;
        _profile = profile;
        _settings = settings;
        _waitSlice = waitSlice ?? DefaultWaitSlice;
        Token = CreateToken();
    }

    /// <summary>Random per process; only the Claude processes started by this app receive it.</summary>
    public string Token { get; }

    public bool IsAuthorized(string? authorizationHeader) =>
        authorizationHeader != null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorizationHeader), Encoding.UTF8.GetBytes("Bearer " + Token));

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Handles one JSON-RPC message or a batch. Returns null when there is nothing to send back (notifications).</summary>
    public async Task<JsonNode?> HandleAsync(Guid jobId, JsonNode message, CancellationToken cancellationToken)
    {
        if (message is JsonArray batch)
        {
            var results = new JsonArray();
            foreach (var item in batch)
            {
                var result = await HandleOneAsync(jobId, item as JsonObject, cancellationToken);
                if (result != null) results.Add(result);
            }
            return results.Count == 0 ? null : results;
        }
        return await HandleOneAsync(jobId, message as JsonObject, cancellationToken);
    }

    private async Task<JsonNode?> HandleOneAsync(Guid jobId, JsonObject? message, CancellationToken cancellationToken)
    {
        if (message == null) return RpcError(null, -32600, "Invalid request");

        var id = message["id"];
        var method = (string?)message["method"];
        if (method == null || id == null) return null; // notification or response

        var parameters = message["params"] as JsonObject ?? new JsonObject();
        try
        {
            switch (method)
            {
                case "initialize":
                    return RpcResult(id, new JsonObject
                    {
                        ["protocolVersion"] = (string?)parameters["protocolVersion"] ?? LatestProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "jobapplier", ["version"] = "1.0.0" },
                        ["instructions"] = "Tools for handing the browser to the human, saving accounts and reporting progress."
                    });

                case "ping":
                    return RpcResult(id, new JsonObject());

                case "tools/list":
                    return RpcResult(id, new JsonObject { ["tools"] = ListTools() });

                case "tools/call":
                    var name = (string?)parameters["name"] ?? "";
                    var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
                    string text;
                    var isError = false;
                    try
                    {
                        text = await CallToolAsync(jobId, name, arguments, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        text = "Error: " + ex.Message;
                        isError = true;
                    }
                    return RpcResult(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = isError
                    });

                default:
                    return RpcError(id, -32601, "Method not found: " + method);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RpcError(id, -32603, ex.Message);
        }
    }

    // ---- tools ----

    public static JsonArray ListTools() => new()
    {
        Tool("wait_for_user",
            "Hand control to the human and wait for them. Use it for: a CAPTCHA / bot check / 'verify you are human' page (kind=captcha - do NOT try to solve it), " +
            "an e-mail verification code or link, 2FA, or a manual sign-in (kind=verification), a required question ON THE APPLICATION FORM that the profile cannot answer (kind=question - " +
            "the answer is saved and reused, so use it only for real form questions), a one-off yes/no decision of your own such as 'the page does not match the job, apply anyway?' (kind=confirm - never saved), " +
            "and the final review before submitting (kind=review). The browser window stays open and visible so the user can act in it. " +
            $"The call returns after at most about {DefaultWaitSlice.TotalSeconds:0} seconds. If the result says the user has not responded yet, call wait_for_user again IMMEDIATELY with the same request_id; " +
            "repeat until the user has responded. Always write the message in English, whatever language the site uses.",
            new JsonObject
            {
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("captcha", "verification", "question", "confirm", "review", "other"),
                    ["description"] = "Why you need the user."
                },
                ["message"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "In ENGLISH, even if the site is in another language: what you see, which site, and exactly what they should do. For a question, just the question translated into English, with its answer options (if any) listed in English."
                },
                ["request_id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Only when continuing a wait: the id returned by the previous call."
                }
            },
            required: new[] { "kind", "message" }),

        Tool("record_account",
            "Call this right after you created an account on a job site (or signed in to an existing one with the JOB_PASSWORD secret). " +
            "The app stores the company, the site, the user's e-mail and the generated password in the user's accounts document. You never see the password.",
            new JsonObject
            {
                ["company"] = new JsonObject { ["type"] = "string", ["description"] = "Company the account is for." },
                ["site_url"] = new JsonObject { ["type"] = "string", ["description"] = "Address of the portal where the account lives." },
                ["note"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: for example 'verification e-mail still pending'." }
            },
            required: new[] { "company", "site_url" }),

        Tool("read_verification_email",
            "Only when the site says it e-mailed a verification code or confirmation link to the user's address (after creating an account or signing in). " +
            "The app looks in the user's mailbox for that mail and returns just the code and/or link - nothing else from the mail. Wait about 15 seconds after the site sent it before the first call; " +
            "if it says nothing has arrived, wait about 20 seconds and call again. If it says it cannot be used, fall back to wait_for_user (kind=verification).",
            new JsonObject { ["site_url"] = new JsonObject { ["type"] = "string", ["description"] = "The address of the site that sent the mail (the page you are on), to recognise it." } },
            required: Array.Empty<string>()),

        Tool("report_progress",
            "Write one short line to the user's activity log, for example 'Found the posting on Greenhouse' or 'Page asks for a cover letter'. Use sparingly.",
            new JsonObject { ["message"] = new JsonObject { ["type"] = "string" } },
            required: new[] { "message" })
    };

    private async Task<string> CallToolAsync(Guid jobId, string name, JsonObject args, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "wait_for_user":
                return await WaitForUserAsync(jobId, args, cancellationToken);

            case "record_account":
                return RecordAccount(jobId, args);

            case "read_verification_email":
                return _verification == null
                    ? "The mailbox cannot be used for this. Call wait_for_user (kind=verification) and tell the user exactly what to do."
                    : await _verification.LookupAsync(jobId, (string?)args["site_url"], cancellationToken);

            case "report_progress":
                var progress = ((string?)args["message"] ?? "").Trim();
                if (progress.Length > 0) _store.Log(jobId, "agent", progress.Length > 400 ? progress[..400] + "..." : progress);
                return "Noted.";

            default:
                throw new InvalidOperationException("Unknown tool: " + name);
        }
    }

    private async Task<string> WaitForUserAsync(Guid jobId, JsonObject args, CancellationToken cancellationToken)
    {
        var kind = ((string?)args["kind"] ?? "other").ToLowerInvariant() switch
        {
            "captcha" => HelpKind.Captcha,
            "verification" => HelpKind.Verification,
            "question" => HelpKind.Question,
            "confirm" => HelpKind.Confirm,
            "review" => HelpKind.Review,
            _ => HelpKind.Other
        };
        var message = ((string?)args["message"] ?? "").Trim();
        var requestId = ((string?)args["request_id"])?.Trim();
        if (message.Length == 0 && string.IsNullOrEmpty(requestId)) throw new ArgumentException("message is required.");

        var overall = TimeSpan.FromMinutes(_settings.Current.HelpTimeoutMinutes);
        var result = await _help.WaitAsync(jobId, kind, message, requestId, _waitSlice, overall, cancellationToken);

        switch (result.Status)
        {
            case HelpWaitStatus.Answered:
                var response = result.Response!;
                return response.Action switch
                {
                    "done" => "The user says they have dealt with it. Take a fresh browser_snapshot and continue from what the page shows now.",
                    "answer" => "The user answered: " + response.Text,
                    "yes" => "The user said YES: go ahead.",
                    "approve" => "The user reviewed the form and APPROVES. Click the final Submit / Send application button now, then confirm the result.",
                    "submitted" => "The user submitted the application themselves. Take a browser_snapshot to confirm if you can, then finish with result Submitted.",
                    "skip" => "The user chose to SKIP this job. Do not submit anything. Finish now with result Skipped.",
                    _ => "The user responded: " + response.Action
                };

            case HelpWaitStatus.Pending:
                return $"The user has not responded yet. Call wait_for_user again right away with request_id=\"{result.Request!.Id}\" (kind and message can be repeated).";

            case HelpWaitStatus.TimedOut:
                return "The user did not respond in time. Stop working on this job and finish with result Blocked, explaining what was needed.";

            case HelpWaitStatus.Cancelled:
                return "The job was cancelled by the user. Stop immediately; do not touch the browser any more.";

            default:
                return "That request is no longer pending. Continue from the current page.";
        }
    }

    private string RecordAccount(Guid jobId, JsonObject args)
    {
        var job = _store.Read(jobId, r => new { r.Password, Company = r.DisplayCompany, Role = r.DisplayTitle });
        if (job == null) throw new InvalidOperationException("Unknown job.");
        if (string.IsNullOrEmpty(job.Password)) throw new InvalidOperationException("No password was generated for this job, so there is nothing to record.");

        var email = _profile.Current.Email;
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("The profile has no e-mail address. Ask the user to add it on the Profile page.");

        var company = ((string?)args["company"])?.Trim();
        var site = ((string?)args["site_url"] ?? "").Trim();
        var record = new AccountRecord
        {
            JobId = jobId,
            Company = string.IsNullOrEmpty(company) ? job.Company : company,
            Role = job.Role,
            Site = AccountService.HostOf(site),
            Email = email,
            Password = job.Password,
            Note = ((string?)args["note"] ?? "").Trim()
        };
        _accounts.Add(record);
        _store.Update(jobId, r => r.AccountRecorded = true);
        _store.Log(jobId, "info", $"Account saved for {record.Company} ({record.Site}) with {email}.");

        return "Saved to the accounts document. Continue with the application. Never type or repeat the password yourself - use the JOB_PASSWORD secret.";
    }

    // ---- JSON-RPC helpers ----

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
        }
    };

    private static JsonObject RpcResult(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    public static JsonObject RpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
