# JobApplier

**Share a screenshot of any job. JobApplier applies for it.**

It works for **any job** - any role, industry or company - and from any source: a LinkedIn or Indeed listing, a company
careers page, a recruiter's message, a post someone shared. If the screenshot shows the role and the company, that is enough.
One screenshot can show a single posting or a whole list of results; every job in it is handled, in parallel.

In short:

1. Add your resume once, on the **Profile** page.
2. Screenshot any job and drop it on the **Dashboard** (or paste it with `Ctrl+V`).
3. Press **Start jobs**. You approve each application before it is sent, unless you turn on auto-submit.

> **Use at your own risk.** Automating applications can get an account flagged or banned on some sites, and an application sent
> in your name is yours. Review-before-submit is on by default for that reason; leave it on until you trust the results.

## What it does

A local ASP.NET Core MVC app that turns **job screenshots into job applications**, using your own Claude or
Google Gemini account (your choice in Settings). Drop in one or many screenshots; for each one it

1. **reads the screenshot** (company, role, location, skills, description). A screenshot of a *list* of jobs is split: every
   job in it becomes its own job, and all of them go through the steps below in parallel,
2. **finds the real posting** on the employer's own careers site / applicant-tracking system,
3. **scores it against your resume** and shows the result in a table,
4. **fills in the application** in its own visible browser window - creating an account with a generated password when the
   site needs one - and
5. **records every account** (company, e-mail from your resume, password) and **every posting it applied to** (company, role,
   URL) in one Word document on your Desktop, and never applies to the same posting twice.

Anything only a person can do (CAPTCHA, e-mail verification, a question your profile cannot answer, the final OK) pauses
that one job and asks you in the dashboard, while the other jobs keep running.

Built with the **Microsoft Agent Framework**, **Claude** (signed in with OAuth through the Claude Code CLI - no API key) or
**Gemini** (through Google's Gemini CLI: paste a free API key, or *Login with Google*) and the **Playwright MCP**
server.

## Requirements

| | |
|---|---|
| .NET 10 SDK | to build and run |
| Node.js 18+ | runs `npx @playwright/mcp` |
| Google Chrome (or Edge) | the browser the agent drives |
| Claude Code CLI | <https://claude.com/claude-code> - needs a Claude Pro / Max / Team / Enterprise login |
| *or* Gemini CLI | installed with one click from Settings (or `npm install -g @google/gemini-cli`) - then paste a Gemini API key or sign in with Google |

## Run it

```powershell
dotnet run --project src/JobApplier
```

It listens on <http://127.0.0.1:5189> only (never on the network) and opens your browser.

First run:

1. **Settings -> pick the AI provider and sign in.** *Sign in with Claude* runs the CLI's own OAuth flow (`claude auth login`);
   For Gemini the quickest way is to paste an API key (see below); *Sign in with Google* is the alternative. JobApplier never sees
   your credentials. Already signed in to Claude Code or Gemini? It is picked up automatically. The header shows the status of
   the provider in use.
2. **Profile -> upload your resume** (PDF, Word, text or image). Claude reads it into the form: name, **e-mail** (used for every
   account), phone, skills, and the full resume text. Correct anything wrong, and fill in what resumes do not say (work
   authorisation, notice period, expected salary) - or leave it blank and be asked when a form needs it. Your file is kept
   unchanged under the name you gave it, and that exact file is what every application uploads (the job log shows its name;
   the Profile page has a link that opens it).
3. **Dashboard -> drop screenshots -> Start jobs.**

The dashboard updates live. A job that needs you shows an orange **Needs you** card with the right buttons, the tab title shows
`(1) Needs you`, and a short beep plays. The browser window of that job is open and visible - solve the CAPTCHA (or do
whatever it asks) in it, then press the button.

### Auto-submit

By default an application is **filled in but not submitted**: the job waits at a *review* step, you look at the form in the
browser window, and press **Looks good - submit it**. Tick *Submit applications automatically* on the dashboard (or make it the
default in Settings) once you trust it. There is also a *minimum match %* to skip poor fits.

### Claude or Gemini

*Settings -> AI provider* chooses what runs the jobs; it applies to the next job. Both providers read the screenshot, search, score,
fill the form and ask you for help in the same way, and both are signed in with your own account. Differences to know about:

* **Result format.** Claude's CLI enforces the typed result of each stage. Gemini's CLI has no such mode, so the format is asked for
  in the prompt and the JSON object is taken from the answer; an answer without a usable object fails the stage (with a note to
  check the employer's site first if it was the Apply step).
* **Tools.** Each Gemini agent gets one built-in tool, a read-only file reader confined to the job's workspace; shell, file
  writing and web tools are refused. Its MCP servers (the browser and this app) come from a settings file in the job's private
  folder, passed through `GEMINI_CLI_SYSTEM_SETTINGS_PATH`, so the app's token is never inside the agent's reach.
* **Signing in to Gemini is three clicks at most.** In *Settings -> Gemini account*:
  1. **Install Gemini CLI** (only shown when it is missing) opens a window that runs `npm install -g @google/gemini-cli`; the page
     notices when it is done.
  2. **Option A - paste an API key** (quickest): create a free key at <https://aistudio.google.com/apikey>, paste it and press *Save key*.
     The app first asks Google whether the key works (so a typo is caught immediately, and a refused key is never saved), then
     saves it as `GEMINI_API_KEY` in `~/.gemini/.env`, the file the Gemini CLI itself reads, so the CLI works the same outside this
     app. It is shown masked (`AIza...ghij`) and *Remove saved key* deletes it. If Google cannot be reached the key is saved
     with a note that it was not checked.
  3. **Option B - sign in with Google**: opens the Gemini CLI; press Enter on *Login with Google*, choose your account in the
     browser, and the app closes that window by itself once the login has landed (no `/quit`).
* **Sign-in state.** Gemini has no "who am I" command: the app shows *signed in* when a cached Google login or an API key variable
  exists. An expired login shows up as a clear error on the first job. *Sign out* removes the cached Google login; an API key in
  the environment is left alone.

## How it works

```
 Browser (dashboard)  --poll-->  ASP.NET Core MVC  ----------------------------------------------+
        ^                          |  JobRunner (queue, N jobs in parallel)                      |
        | Needs you / answers      v                                                             |
        |                  Agent Framework workflow, one per job                                 |
        |        Extract --> Find --> Match --> Apply        (conditional edges stop the chain)  |
        |           |         |        |         |                                               |
        |           v         v        v         v        each stage = a ClaudeCodeAgent         |
        |       claude -p  (Claude Code CLI, your OAuth login, --restricted, no shell tools)     |
        |           |  \                                                                         |
        |           |   +--> Playwright MCP  (npx @playwright/mcp, own visible browser per job)  |
        |           +------> JobApplier MCP  (http://127.0.0.1:5189/mcp/{jobId}, bearer token)   |
        |                       wait_for_user . record_account . report_progress                 |
        +-------------------------------------------------------------------------------------+
```

* **Claude with OAuth.** A subscription login can only be used through the official CLI, so `ClaudeCodeAgent` is a Microsoft
  Agent Framework `AIAgent` whose model and tool loop is `claude -p --output-format stream-json`. Because it is a normal
  `AIAgent`, typed results work with `RunAsync<T>` (via the CLI's `--json-schema`), and the stages are workflow executors.
* **Locked down.** Each agent runs with `--restricted --strict-mcp-config --permission-mode dontAsk`: no shell or code tools,
  only the two MCP servers above (never your own MCP setup), file access confined to the job's workspace folder, and
  anything not explicitly allowed is denied rather than prompted.
* **Parallel.** Each job is its own workflow with its own Claude process, its own Playwright MCP server and its own
  browser window. *Settings -> Jobs at the same time* (default 3, at most 8) is the limit; extra jobs queue. A screenshot that
  lists several jobs is split when it has been read: the first job stays with the screenshot's job, every other one is a new job
  (same image, details already filled in) that joins the queue at once. Repeated entries in one screenshot (a list next to the
  details pane of the selected job) count once. Two screenshots that overlap are not merged when they are read, but the
  duplicate guard below still skips the second application.
* **Sign-in fails -> new account.** If signing in does not work (wrong password, no such account), the agent creates a new
  account with your e-mail and the generated password instead, and records it. Only when the site says the e-mail is already
  registered - an account exists whose password the app does not have, and it cannot read your mailbox to reset it - does the
  job pause and ask you.
* **Passwords never reach the model.** The app generates a strong 16-character password per site and hands it to Playwright
  through its `--secrets` file. The agent only ever types the *name* `JOB_PASSWORD`; Playwright substitutes the real value
  inside the browser tool, and tool output is redacted. The password is also masked from the job log and never written to
  `job.json`. If a later job uses a site that already has an account, the saved password is reused and the agent signs in.
* **Human in the loop.** `wait_for_user` returns after at most 45 s with *not yet*, and the agent calls it again. It must stay
  under the Claude CLI's own 60 s limit for a tool call: a longer wait is aborted by the client, which then re-asks on its own
  and can lose your answer. A wait can last as long as you need (default limit 45 min, then the job gives up).
* **English only.** Everything a job asks you - the *Needs you* card, form questions, summaries - is written in English, even
  when the employer's site is in Spanish or another language. The agent translates the question (and its options) for you and
  translates your answer back into the form's language.
* **Clearing failed jobs.** Every job is kept until you delete it, and a job that was running when the app stopped shows as
  *Failed (interrupted)*. *Clear failed* in the Results header deletes all *Failed* jobs and their files at once (after a
  confirmation). Running and queued jobs, other results, accounts and the applications list are not touched.
* **Retry all failed.** The button in the Results header queues every *Failed* job again, oldest first (a few at a time, as set in
  Settings); finished stages are kept and *Applied* jobs are never touched. If a limit is still in force the jobs fail again and
  the queue is held back, so nothing is lost by pressing it early - *Retry when available* is the patient version.
* **Usage limits.** If the AI runs out of tokens or quota (Claude's "5-hour / weekly limit reached", Gemini's "429 RESOURCE_EXHAUSTED" and
  similar), the job is marked *usage limit* instead of just *Failed*, with the reset time when the error gives one. New jobs that
  have not started yet are held back until then (a banner says so) instead of all failing the same way. On the job page or the
  dashboard, **Retry when available** makes the job retry by itself once the limit has reset - also after an app restart, because
  the state is saved. If no reset time was given it probes again after 3, 10, 20, 30 then 60 minutes, and gives up after 8
  automatic attempts. **Stop waiting** cancels it; **Retry now** retries immediately. Waiting jobs are never removed by *Clear
  failed*. A limit during the Apply step may happen after the form was already submitted, so the job says to check the employer's
  site before retrying.
* **Resumable.** Finished stages are stored on the job. *Retry* continues from the stage that failed; a *not found* job searches
  again. An *Applied* job can never be retried.
* **No double applications.** Two jobs for the same posting (same URL, ignoring tracking parameters) never both submit: the
  second is skipped. The check and the claim happen under one lock, so parallel jobs cannot race. Every submitted posting is
  also written to `applications.json` and the Desktop document, which outlive the job: deleting a job does not make its
  posting applicable again.
* **LinkedIn is never automated.** Screenshots are the only LinkedIn input; the agent is told never to log in to or browse
  LinkedIn, because that breaks its terms and risks your account. It works on the employer's own site.

### The match score

The agent scores five dimensions 0-100 from evidence in the resume; the app computes the percentage itself so it is stable:

`skills 40% + experience 25% + domain 15% + seniority 10% + logistics 10%`

The details page shows each part, the skills you have, and the gaps.

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

## Gmail replies

Turn on reply tracking (the **Start tracking replies** button on the dashboard, or *Settings -> Gmail replies*) and the dashboard shows
**which employers answered** each application, and what they said: **received**, **moving forward** (interview, call, assessment, next steps), **info requested**, **rejected** or **offer**.
The results table has an **Employer reply** column: each application shows its latest answer (with the subject and how long ago), or
*no reply yet*; the *Replies from employers* card above it lists the mails with a link that opens each in Gmail. It works in English and Spanish.

**There is no Google sign-in and no Google Cloud project.** Claude reads your Gmail through the **Gmail connector of your Claude
account**, the same one you can use in Claude itself. To set it up:

1. Be signed in to Claude (the Claude card in Settings).
2. Connect Gmail to your Claude account: press *Open Claude to connect Gmail* (a Claude window opens on its `/mcp` screen: choose
   *claude.ai Gmail*, then *Authenticate*), or add *Gmail* at claude.ai -> Settings -> Connectors. Then press *Check connection*.
3. Press *Start tracking replies* (on the dashboard or here). It first checks that Claude can really use Gmail and, if not, says what is
   missing instead of turning anything on.

Nothing reads your mailbox until you press *Start tracking replies*. After that it is checked by itself every few hours (6 by default,
0 = only when you press *Check for replies now*; set in Settings), and never while the Claude usage limit is holding jobs back.

How it works, and what to know:

* **The connector status comes from a live Claude session**, not from `claude mcp list`, which can report a connector as "Connected"
  that a real session cannot use (it is then `needs-auth`). The app starts Claude just long enough to read which connectors it has,
  then stops it.
* **Read-only, enforced by name.** A scan runs Claude with *only* the connector's tools that search and read mail
  (`GmailToolPolicy`), no file, shell or web tools, and a permission mode that refuses everything else. Any Gmail tool that could send,
  draft, label, move or delete is refused explicitly. Mail text is treated as data: the prompt tells Claude never to follow
  instructions found in a message, and even a message that tried would have nothing to act with.
* **Your mail is processed by Claude (Anthropic)**, under your Claude account, like anything else you ask Claude about your mail. It is
  not sent anywhere else, and no other AI sees it (the Gemini provider is not involved). JobApplier keeps only the sender, subject, a
  short snippet and the verdict of each reply (`replies.json`), never the messages.
* **It uses some of your Claude usage.** A small model reads the mailbox (Haiku by default, changeable in Settings) and only the last
  few days are looked at after the first check.
* **Claude decides which application a mail answers and what it says**, so it can be wrong. What it reports is checked before it is
  stored (it must name one of the applications asked about, have a message id and not predate the application). A doubtful reply
  carries a *check* tag, and *Not about this job* removes a wrong match for good.

## Settings

| Setting | Meaning |
|---|---|
| AI provider | `claude` (Claude Code CLI) or `gemini` (Gemini CLI). |
| Claude model / Effort | Claude model alias (`sonnet`, `opus`, `haiku`, `fable`) or id; effort `low`...`max`. Empty = Claude Code default. |
| Gemini model | a Gemini model id such as `gemini-2.5-pro`. Empty = the Gemini CLI's default. |
| Jobs at the same time | 1-8, default 3. |
| Browser / Headless | `chrome` (default), `msedge`, `firefox`, `webkit`. Headless hides the window - you cannot solve a CAPTCHA you cannot see. |
| Playwright MCP version | pinned (`0.0.85`) so a new release cannot change behaviour under you. |
| Defaults | auto-submit and minimum match % pre-filled on the dashboard. |
| Help timeout | how long a job waits for you before giving up. |
| Mail model / Check every | the model that reads the mailbox for replies (default `haiku`) and how often, in hours, it is checked once tracking is on (default 6, 0 = only when asked). |
| Accounts document / Claude CLI path / Gemini CLI path | override the defaults. |

## Where things are stored

`%LOCALAPPDATA%\JobApplier` (override with `JobApplier:DataFolder`):

```
settings.json  profile.json  accounts.json  applications.json
replies.json  mail-state.json   employer replies found in Gmail, and whether tracking is on and when it last ran
mail-workspace/              an empty folder the mailbox reader runs in
resume/                      your resume copy
jobs/<id>/job.json           the job: results, log (passwords never included)
jobs/<id>/screenshot.*       the uploaded image
jobs/<id>/workspace/         the agent's working folder (screenshot, resume copy, browser output)
jobs/<id>/private/           MCP config + secrets file - outside the workspace, so the agent cannot read it
```

## Security and privacy

* The UI and the MCP endpoint bind to loopback only; `AllowedHosts` is `localhost;127.0.0.1`. Forms use anti-forgery tokens and the
  page has a strict Content-Security-Policy (no inline scripts or styles). The MCP endpoint needs a random per-process bearer token.
* Uploaded "screenshots" are checked by content, not by file name.
* Web pages, screenshots and job descriptions are treated as **untrusted data**: the agent is instructed never to follow
  instructions found in them, never to type your details anywhere but the application form, and never to pay, download or
  install anything. Instructions are a strong default, not a guarantee - which is one more reason to leave auto-submit off until
  you have watched a few runs.
* **Your resume and profile are sent to the AI provider you selected (Anthropic for Claude, Google for Gemini)** because the agent
  needs them to fill forms and score fit. Nothing is sent anywhere else.
* The job log shows what the agent *did* ("typed into *Password field*"), never what it typed.
* Gmail (optional) is read by Claude through your Claude account's Gmail connector, with read-only tools only - see *Gmail replies*
  above. This app holds no Google credential. The mail text is processed by Claude (Anthropic), is never logged, and only a short
  snippet and the verdict of each reply are kept on this PC.

## Things to know

* **E-mail verification:** the job agent cannot read your inbox (the optional reply tracking is a separate, read-only Claude run that
  only reports replies, and is not available to the job agent). When a site sends a code or link it asks you to do it in the browser window, then continues.
* **Not every site works.** Standard forms on Greenhouse, Lever, Ashby, SmartRecruiters and many company sites do; heavily
  customised portals, "Easy Apply inside LinkedIn", or sites that block automation may need your help or fail. A failed job keeps its
  log, so you can see how far it got.
* The agent answers form questions only from your profile and saved answers. Eligibility and legal questions it cannot answer
  are **asked, not guessed**; your answer is saved and reused (one-off confirmations are not).
* Voluntary self-identification questions are answered *prefer not to say* unless your profile says otherwise.
* Each stage costs usage from your Claude subscription or Gemini account; a typical job is a few minutes of agent time.

## Project layout

```
src/JobApplier/
  Agents/     Prompts, AgentFactory, JobWorkflow (the four stages as Agent Framework executors)
  Claude/     CliAgent (the shared AIAgent base), ClaudeCodeAgent + ClaudeCli, GeminiCliAgent + GeminiCli + GeminiStream
              (decoder, settings file), tool-activity log lines
  Mail/       Gmail replies: ClaudeMailReader (connector probe + read-only scan), GmailToolPolicy, MailScanPrompt, MailChecker +
              MailWatcher, ReplyStore
  Mcp/        JobMcpServer - wait_for_user, record_account, report_progress
  Services/   JobRunner (queue, usage-limit hold + auto-retry), JobStore, HumanHelpBroker, AccountService + AccountsDocument (.docx),
              ProfileService/Importer, Claude/GeminiAccountService, ...
  Controllers/ Views/ wwwroot/   the dashboard, job details, profile and settings pages
tests/JobApplier.Tests/   xUnit
```

## Tests

```powershell
dotnet test
```

The unit tests cover password generation and redaction, the stream parser, the MCP protocol, the help broker, the Word document
writer, account reuse, the duplicate guard, the JSON schemas the CLI accepts, and how the Agent Framework routes messages
between stages. The Gmail part is covered against a fake `claude` CLI (a script that answers from a scenario file and records how it was
called): the connector probe, the read-only tool restriction and the exact arguments of a scan, a connector that needs sign-in or
drops out, usage limits, timeouts, and what is accepted from Claude's answer.

The Gmail pages were also driven in a real browser (Settings card, the replies card). What has **not** been tested is a live read of a
real mailbox, because that needs your Gmail connector to be signed in in your Claude account.

The whole pipeline was also checked live against the real Claude CLI, a real browser and a local fake company portal (CAPTCHA
wall, sign-up with a password policy, file upload, a question the profile could not answer, a review gate): three jobs in
parallel, cancellation (no `claude` / browser processes left behind), account reuse, and the duplicate guard.
