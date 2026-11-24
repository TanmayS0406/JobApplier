# Configuration and data

[← Back to the README](../README.md)

## Settings

| Setting | Meaning |
|---|---|
| AI provider | `claude` (Claude Code CLI) or `copilot` (GitHub Copilot CLI). |
| Claude model / Effort | Claude model alias (`sonnet`, `opus`, `haiku`, `fable`) or id; effort `low`...`max`. Empty = Claude Code default. |
| GitHub Copilot model | passed as `--model`, for example `auto`. Empty = the Copilot CLI default. |
| Jobs at the same time | 1-8, default 3. |
| Browser / Headless | `chrome` (default), `msedge`, `firefox`, `webkit`. Headless hides the window - you cannot solve a CAPTCHA you cannot see. |
| Playwright MCP version | pinned (`0.0.85`) so a new release cannot change behaviour under you. |
| Defaults | auto-submit, minimum match % and "only apply if the job offers visa sponsorship or relocation" pre-filled on the dashboard. |
| Help timeout | how long a job waits for you before giving up. |
| Mail model / Check every | the model that reads the mailbox for replies (default `haiku`) and how often, in hours, it is checked once tracking is on (default 6, 0 = only when asked). |
| Accounts document / Claude CLI path / GitHub Copilot CLI path | override the defaults. |

## Where things are stored

The jobs and the queue are in the SQL Server database (see [The database and the queue](queue-and-database.md)): the `Jobs` table holds each job - its status,
the results of each stage and the log (passwords are never stored) - and Hangfire's tables are in the `HangFire` schema.

The files are in `%LOCALAPPDATA%\JobApplier` (override with `JobApplier:DataFolder`):

```
settings.json  profile.json  accounts.json  applications.json
replies.json  mail-state.json   employer replies found in Gmail, and whether tracking is on and when it last ran
resume-advice.json           the last advice of the resume coach (Insights)
mail-workspace/              an empty folder the mailbox reader runs in
resume/                      your resume copy
jobs/<id>/job.json.imported  the job as an earlier version saved it (only if you upgraded; a backup, no longer read)
jobs/<id>/screenshot.*       the uploaded image
jobs/<id>/workspace/         the agent's working folder (screenshot, resume copy, browser output)
jobs/<id>/private/           MCP config + secrets file - outside the workspace, so the agent cannot read it
```

## The accounts document

`JobApplier-Accounts.docx` on your Desktop (change it in Settings), with two tables:

* **Accounts** - one row per **site and e-mail**: company, role(s), site, e-mail, password, date.
* **Applications** - one row per **posting applied to**: company, role, posting URL, result (*Submitted* or *Already applied*),
  date. A new job whose posting URL matches a row here is skipped. The skip check reads `applications.json`, not the
  document, so editing the document does not change what is skipped; to apply to a posting again on purpose, delete its entry
  from `applications.json` (with the app stopped) and press *Rebuild document*.

It is rebuilt from `accounts.json` and `applications.json` on every change, so if Word has it open the data is safe and
*Settings -> Rebuild document* writes it once you close Word.

**It contains passwords in plain text.** Keep it private (or move it somewhere encrypted).
