# Getting started in detail

[← Back to the README](../README.md)

## First run

1. **Settings -> pick the AI provider and sign in.** *Sign in with Claude* runs the CLI's own OAuth flow (`claude auth login`);
   *Sign in with GitHub* does the same for GitHub Copilot (`copilot login`; [step by step](ai-providers.md)). JobApplier never sees
   your credentials. Already signed in to Claude Code or the Copilot CLI? It is picked up automatically. The header shows the status of
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

## Auto-submit

By default an application is **filled in but not submitted**: the job waits at a *review* step, you look at the form in the
browser window, and press **Looks good - submit it**. Tick *Submit applications automatically* on the dashboard (or make it the
default in Settings) once you trust it. There is also a *minimum match %* to skip poor fits.
