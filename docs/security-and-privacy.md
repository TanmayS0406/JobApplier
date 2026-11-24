# Security, privacy and known limits

[← Back to the README](../README.md)

## Security and privacy

* The UI and the MCP endpoint bind to loopback only; `AllowedHosts` is `localhost;127.0.0.1`. Forms use anti-forgery tokens and the
  page has a strict Content-Security-Policy (no inline scripts or styles). The MCP endpoint needs a random per-process bearer token.
* Uploaded "screenshots" are checked by content, not by file name.
* Web pages, screenshots and job descriptions are treated as **untrusted data**: the agent is instructed never to follow
  instructions found in them, never to type your details anywhere but the application form, and never to pay, download or
  install anything. Instructions are a strong default, not a guarantee - which is one more reason to leave auto-submit off until
  you have watched a few runs.
* **Your resume and profile are sent to the AI provider you selected (Anthropic for Claude, GitHub and Microsoft for GitHub Copilot)** because the agent
  needs them to fill forms and score fit. Nothing is sent anywhere else.
* The job log shows what the agent *did* ("typed into *Password field*"), never what it typed.
* Gmail (optional) is read by Claude through your Claude account's Gmail connector, with read-only tools only - see [Gmail replies](gmail-replies.md).
  This app holds no Google credential. The mail text is processed by Claude (Anthropic), is never logged, and only a short
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
* Each stage costs usage from your Claude subscription or GitHub Copilot account; a typical job is a few minutes of agent time.
