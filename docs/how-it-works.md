# How it works

[← Back to the README](../README.md)

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
  the database. If a later job uses a site that already has an account, the saved password is reused and the agent signs in.
* **Human in the loop.** `wait_for_user` returns after at most 45 s with *not yet*, and the agent calls it again. It must stay
  under the Claude CLI's own 60 s limit for a tool call: a longer wait is aborted by the client, which then re-asks on its own
  and can lose your answer. A wait can last as long as you need (default limit 45 min, then the job gives up).
* **English only.** Everything a job asks you - the *Needs you* card, form questions, summaries - is written in English, even
  when the employer's site is in Spanish or another language. The agent translates the question (and its options) for you and
  translates your answer back into the form's language.
* **Clearing failed jobs.** Every job is kept until you delete it. *Clear failed* in the Results header deletes all *Failed* jobs and
  their files at once (after a confirmation). Running and queued jobs, jobs waiting for a usage limit, other results, accounts and the
  applications list are not touched. (A job that was running when the app stopped is not failed: it continues when the app starts.)
* **Retry all failed.** The button in the Results header queues every *Failed* job again, oldest first (a few at a time, as set in
  Settings); finished stages are kept and *Applied* jobs are never touched. If a limit is still in force the jobs fail again and
  the queue is held back, so nothing is lost by pressing it early - *Retry when available* is the patient version.
* **Choose how a retry applies.** Every Retry button (*Retry all failed*, *Retry when available*, and *Retry* / *Retry now* on a job's
  page) opens a small dialog with the same choices as the upload form: **Submit applications automatically**, **Skip jobs that
  match less than N%** and **Only apply if the job offers visa sponsorship or relocation**. The dashboard buttons start from the upload form's defaults (Settings); a single job's button starts from that
  job's own settings. What you choose replaces the job's settings before it is queued again, and is written in the job log. The
  minimum is checked against the match score the job already has (finished work is kept), so lowering it lets a job that was skipped
  for a low score go on, and raising it skips one that would have applied. *Retry when available* saves the choices on the job now and
  the automatic retry uses them later. *Apply anyway* still ignores the minimum and the visa / relocation requirement.
* **Usage limits.** If the AI runs out of tokens or quota (Claude's "5-hour / weekly limit reached", GitHub Copilot's rate-limit and quota errors and
  similar), the job is marked *usage limit* instead of just *Failed*, with the reset time when the error gives one. New jobs that
  have not started yet are held back until then (a banner says so, and they are scheduled in Hangfire for after it) instead of all
  failing the same way. **The job that hit the limit retries by itself once the limit has reset**, continuing from the stage it
  stopped at - no button to press, and also after an app restart, because the state is saved in the database. If no reset time was
  given it probes again after 3, 10, 20, 30 then 60 minutes (hourly after that), and gives up after 200 automatic attempts (about a
  week). **Stop waiting** cancels it; **Retry now** retries immediately. *Retry when available* is still there for a job you stopped
  waiting for. Waiting jobs are never removed by *Clear failed*. A limit during the Apply step may happen after the form was already
  submitted, so the job says to check the employer's site before retrying.
* **Resumable.** Finished stages are stored on the job. *Retry* continues from the stage that failed; a *not found* job searches
  again. An *Applied* job can never be retried.
* **No double applications.** Two jobs for the same posting (same URL, ignoring tracking parameters) never both submit: the
  second is skipped. The check and the claim happen under one lock, so parallel jobs cannot race. Every submitted posting is
  also written to `applications.json` and the Desktop document, which outlive the job: deleting a job does not make its
  posting applicable again.
* **LinkedIn is never automated.** Screenshots are the only LinkedIn input; the agent is told never to log in to or browse
  LinkedIn, because that breaks its terms and risks your account. It works on the employer's own site.

## The match score

The agent scores five dimensions 0-100 from evidence in the resume; the app computes the percentage itself so it is stable:

`skills 40% + experience 25% + domain 15% + seniority 10% + logistics 10%`

The details page shows each part, the skills you have, and the gaps.

## Only apply where the employer helps you move

Tick **Only apply if the job offers visa sponsorship or relocation** on the dashboard (or make it the default in Settings) and a job is
applied for only when its posting says the employer sponsors work visas **or** helps with relocation.

* **Where the answer comes from.** The matcher reads the job description (the full posting from the employer's site when it was found,
  otherwise what the screenshot showed) and reports two facts, each *offered*, *not offered* or *not mentioned*: visa sponsorship, and
  relocation help, with the sentence that shows it. The decision is made by the app from those answers, not by the model.
* **What counts as an offer.** "Visa sponsorship available" or "relocation package" is an offer. "No sponsorship" or "must already be
  authorised to work without sponsorship" is a refusal. "Relocation required" or "must be willing to relocate" is **not** an offer of
  help, because it asks something of you.
* **A posting that says nothing is skipped.** The option means "the posting must say so", so a job that does not mention either is
  skipped, with a status that says what the posting said. A screenshot of a list entry carries little text, which is another reason the
  real posting is read first. The details page shows both answers and the quoted sentence.
* **Order of the checks.** The minimum match is judged first, then this requirement. A job that fails either is *Skipped* and nothing
  is applied for.
* **Changing your mind.** *Apply anyway* on a skipped job lifts both the minimum match and this requirement for that job. The Retry
  dialog has the same checkbox, and a job scored before this option existed is scored again to find out what its posting says (a score
  that is kept is not asked for again).
* **Jobs split from a list** inherit the choice, like auto-submit and the minimum.

## Long lists: pages, sort by match, filter by match

The *Results* table and the *Replies from employers* card are shown a page at a time (10, 25, 50 or 100 per page). Under each title:

* **Sort**: newest first (the default), **match high to low**, **match low to high** (jobs also: company A-Z). A job that has no match
  percentage yet always comes last, whichever way the percentage is sorted.
* **Match at least**: only items whose match percentage is at or above 50, 60, 70, 80 or 90%. A job that has not been scored yet is left
  out while a filter is on. A reply has the match of the job it answers (shown on the reply as "87% match").
* The totals ("27 jobs", "Retry all failed (8)", the reply counts per kind) always cover **everything**, not only the page you are on.

The page, sort and filter are kept in the address (`jobs_page`, `jobs_sort`, `replies_min`, ...), so a reload or a copied link shows the
same page, and the list keeps refreshing by itself without jumping back to the first page.
