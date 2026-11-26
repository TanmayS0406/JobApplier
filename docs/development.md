# Development

[← Back to the README](../README.md)

## Project layout

```
src/JobApplier/
  Agents/     Prompts, AgentFactory, JobWorkflow (the four stages as Agent Framework executors)
  Claude/     CliAgent (the shared AIAgent base), ClaudeCodeAgent + ClaudeCli, CopilotCliAgent + CopilotCli + CopilotStream
              (event decoder, MCP config), JsonExtract, tool-activity log lines
  Mail/       Gmail replies: ClaudeMailReader (connector probe, read-only scan, draft, and the one send), GmailToolPolicy,
              MailScanPrompt, MailChecker + MailWatcher, ReplyStore, ReplyComposer (draft / save / send with the double-send
              guards) + ReplyPrompts (draft and send prompts, ReplyText)
  Insights/   resume gap analysis: SkillNormalizer, TF-IDF + K-Means (ML.NET), calibration + coverage plan (Math.NET), ResumeCoach
  Mcp/        JobMcpServer - wait_for_user, record_account, report_progress
  Persistence/ the EF Core model (JobApplierDbContext), Migrations/, EfJobPersistence, start-up migration, import of the old job.json files
  Services/   JobRunner (the Hangfire job: parallel limit, usage-limit hold + auto-retry, resume after a restart), JobQueue (Hangfire),
              JobStore, HumanHelpBroker, AccountService + AccountsDocument (.docx), ProfileService/Importer,
              Claude/CopilotAccountService, ...
  Controllers/ Views/ wwwroot/   the dashboard, job details, profile and settings pages
tests/JobApplier.Tests/   xUnit
```

## Tests

```powershell
dotnet test
```

The unit tests cover password generation and redaction, the stream parser, the MCP protocol, the help broker, the Word document
writer, account reuse, the duplicate guard, the JSON schemas the CLI accepts, and how the Agent Framework routes messages
between stages. The queue is covered without any AI agent or SQL Server: the EF mapping runs on a throw-away SQLite file per test,
Hangfire is replaced by a recording queue, and the tests check queuing, cancelling, the parallel limit, a limit that ends and brings
the job back, the app being closed mid-run, and a restart that continues from the stage reached. Separate tests check that the
migrations match the model and never use the `json` column type that SQL Server 2022 lacks. The Gmail part is covered against a fake `claude` CLI (a script that answers from a scenario file and records how it was
called): the connector probe, the read-only tool restriction and the exact arguments of a scan, a connector that needs sign-in or
drops out, usage limits, timeouts, and what is accepted from Claude's answer. The same fake models a send: the draft run is checked
never to be allowed the reply tool, the send run to be allowed that tool alone, and "sent" to follow Gmail's tool result (refused, no answer
at all, an answer that arrives after a failed run) rather than Claude's own claim.

The GitHub Copilot provider is covered the same way against a fake `copilot` (Windows with Node.js on the PATH; otherwise those tests
return early): the exact arguments and permission rules a run starts with, the task file and its removal, the MCP config kept out of the
agent's reach, attachments, typed results, a missing sign-in, a rate limit, and an answer without the asked-for object. The event
decoder is tested on lines shaped like the CLI's own event definitions. The visa / relocation option has tests for the gate itself, the
re-scoring of older scores, "Apply anyway", jobs split from a list, the retry dialog's checkbox, and reading a stored score saved before the
option existed. What has **not** been tested is a live run against a signed-in Copilot account.

The Gmail pages were also driven in a real browser (Settings card, the replies card). What has **not** been tested is a live read of a
real mailbox, because that needs your Gmail connector to be signed in in your Claude account.

The whole pipeline was also checked live against the real Claude CLI, a real browser and a local fake company portal (CAPTCHA
wall, sign-up with a password policy, file upload, a question the profile could not answer, a review gate): three jobs in
parallel, cancellation (no `claude` / browser processes left behind), account reuse, and the duplicate guard.
