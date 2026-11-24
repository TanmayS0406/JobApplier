# Claude or GitHub Copilot

[← Back to the README](../README.md)

The header shows both: one badge per provider (*Claude: ...*, *Copilot: ...*) saying whether its CLI is installed and signed in, and the
one that runs the jobs is marked *in use*.

*Settings -> AI provider* chooses what runs the jobs; it applies to the next job. Both providers read the screenshot, search, score,
fill the form and ask you for help in the same way, and both are signed in with your own account (OAuth: no API key is kept by this
app). Differences to know about:

* **Result format.** Claude's CLI enforces the typed result of each stage. The Copilot CLI has no such mode, so the format is asked for
  in the prompt and the JSON object is taken from the answer; an answer without a usable object fails the stage (with a note to
  check the employer's site first if it was the Apply step).
* **How the task is handed over.** Copilot cannot take a system prompt, and its `-p` option cannot be combined with a piped prompt. The
  instructions, the task and the result format are written to one file in the job's workspace, and the prompt names it with `@`, so
  there is no command-line length limit. The file is removed when the run ends (it holds your resume and profile text).
* **Tools.** Claude runs with `--restricted`. Copilot has no such switch, so its limits are permission rules: in `-p` mode anything not
  allowed is refused rather than asked about, and only the browser server, this app's own server and read-only file viewing are
  allowed. Shell commands, file writes, URL access and memory are denied outright (a deny rule wins over everything), file access is
  the job's workspace and nothing else (not even the temp folder), and the CLI's own GitHub server and any MCP servers you set up in
  Copilot yourself are switched off for the run. Your GitHub token variables are hidden from the browser server's environment.
* **MCP servers** come from a config file in the job's private folder (passed with `--additional-mcp-config`), so the app's bearer
  token is never inside the agent's reach.
* **Screenshots and resumes.** Images and PDFs the task names are attached with `--attachment`, which is how the CLI is given a file it
  cannot read as text. A Word document is not attached and the file viewer reads text, so importing a resume from a `.docx` may not work
  while Copilot is selected: use a PDF or an image of it, or switch to Claude for the import.
* **Sessions.** A Claude stage can resume its session; a Copilot stage always starts a fresh one (finished stages are still kept on the
  job, so a retry continues from the stage that failed either way).
* **Model.** *Settings -> GitHub Copilot model* is passed as `--model`. `auto` lets Copilot choose; empty uses the CLI's default.

## Signing in to GitHub Copilot

It needs an active GitHub Copilot subscription (your own, or one your organisation gives you; an organisation can also switch the CLI
off). In *Settings -> GitHub Copilot account*:

1. **Install GitHub Copilot CLI** (only shown when it is missing) opens a window that runs `winget install GitHub.Copilot`, or
   `npm install -g @github/copilot` when winget is not there (npm needs Node.js 22 or newer); the page notices when it is done.
2. **Sign in with GitHub** opens a window running `copilot login`. Your browser opens on GitHub to authorize *GitHub Copilot CLI* (or the
   window shows a one-time code to enter at github.com/login/device). The CLI keeps the token, in the system credential store.
   JobApplier never sees it.
3. **Prefer a token** (a server, a script)? Set `COPILOT_GITHUB_TOKEN` to a fine-grained personal access token with the *Copilot
   Requests* permission. `GH_TOKEN` and `GITHUB_TOKEN` also work, in that order. Classic tokens (`ghp_...`) are not accepted by the CLI.
   A token variable silently overrides a stored sign-in, so if the wrong account is used, check the environment first.

* **Sign-in state.** The Copilot CLI has no "who am I" command: the app shows *signed in* when a token variable is set, or when a sign-in
  made with `copilot login` is stored (an entry of the CLI in the Windows Credential Manager, or a token in `~/.copilot/config.json`
  when the CLI had no credential store). That says a sign-in exists, not that it is still valid: an expired or revoked one, a missing
  subscription or an organisation policy shows up as a clear error on the first job, with the same pointer back to the sign-in button.
* **Sign out.** The CLI can only be signed out from inside itself, so *Sign out* opens it with the instruction to type `/logout`. A
  sign-in that comes from a token variable is not signed out from here; remove the variable.
* **Usage limits.** Copilot's rate-limit and quota errors are recognised by the CLI's own error kind, so the job waits for the reset and
  continues by itself, like with Claude (see [How it works](how-it-works.md)).

## How well is this tested?

The Copilot provider was built against the CLI's documented interface and the event definitions the CLI ships with (version 1.0.114),
and it is covered by tests that run a stand-in `copilot` which records exactly how it was called and prints the events a real run
prints: the arguments and permission rules, the task file, the MCP config, attachments, typed results, and sign-in, limit and error
handling. **It has not been run against a live Copilot account yet.** If a job misbehaves, the job log shows what the CLI did; please open an
issue with it (leave out anything private).

## Coming from an earlier version

An earlier version could also run on Gemini. That support is gone. A setting saved with Gemini starts the app on Claude instead, and the
other Gemini settings are ignored; pick *GitHub Copilot* in Settings to use it.
