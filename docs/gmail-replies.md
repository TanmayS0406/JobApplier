# Gmail replies

[← Back to the README](../README.md)

Turn on reply tracking (the **Start tracking replies** button on the dashboard, or *Settings -> Gmail replies*) and the dashboard shows
**which employers answered** each application, and what they said: **received**, **moving forward** (interview, call, assessment, next steps), **info requested**, **rejected** or **offer**.
The results table has an **Employer reply** column: each application shows its latest answer (with the subject and how long ago), or
*no reply yet*; the *Replies from employers* card above it lists the mails with a link that opens each in Gmail. It works in English and Spanish.

**There is no Google sign-in and no Google Cloud project.** Claude reads your Gmail through the **Gmail connector of your Claude
account**, the same one you can use in Claude itself. To set it up:

1. Be signed in to Claude (the Claude card in Settings).
2. Connect Gmail to your Claude account: press *Open Claude to connect Gmail* (a Claude window opens; once its prompt is up, type `/mcp` - right after start-up the connectors are not loaded yet - and choose
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
  not sent anywhere else, and no other AI sees it (the GitHub Copilot provider is not involved). JobApplier keeps only the sender, subject, a
  short snippet and the verdict of each reply (`replies.json`), never the messages.
* **It uses some of your Claude usage.** A small model reads the mailbox (Haiku by default, changeable in Settings) and only the last
  few days are looked at after the first check.
* **Claude decides which application a mail answers and what it says**, so it can be wrong. What it reports is checked before it is
  stored (it must name one of the applications asked about, have a message id and not predate the application). A doubtful reply
  carries a *check* tag, and *Not about this job* removes a wrong match for good.
