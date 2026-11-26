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
* **Reading is read-only, enforced by name.** A scan - and the drafting of an answer, below - runs Claude with *only* the connector's tools
  that search and read mail (`GmailToolPolicy`), no file, shell or web tools, and a permission mode that refuses everything else. Any
  Gmail tool that could send, draft, label, move or delete is refused explicitly. Mail text is treated as data: the prompt tells Claude
  never to follow instructions found in a message, and even a message that tried would have nothing to act with.
* **Sending is a separate step that only your click starts.** See *Answering an employer* below.
* **Your mail is processed by Claude (Anthropic)**, under your Claude account, like anything else you ask Claude about your mail. It is
  not sent anywhere else, and no other AI sees it (the GitHub Copilot provider is not involved). JobApplier keeps only the sender, subject, a
  short snippet and the verdict of each reply (`replies.json`), never the messages.
* **It uses some of your Claude usage.** A small model reads the mailbox (Haiku by default, changeable in Settings) and only the last
  few days are looked at after the first check.
* **Claude decides which application a mail answers and what it says**, so it can be wrong. What it reports is checked before it is
  stored (it must name one of the applications asked about, have a message id and not predate the application). A doubtful reply
  carries a *check* tag, and *Not about this job* removes a wrong match for good.

## Showing only some of them

Both lists have a **Show** menu next to *Sort*, so you can look at one kind of answer at a time. It is kept in the address, like the
page and the sort, and *Clear filter* resets it together with *Match at least*.

* **Replies from employers:** *Rejected*, *Info requested*, *Moving forward*, *Offer*, *Received* or *Unclear*.
* **Results table:** *Got a reply* (any), *Rejected*, *Info requested*, *Moving forward*, *Offer*, *No reply yet* (applied, nothing back),
  and *Skipped* (jobs that were not applied for, for example below your minimum match). A job is filed by its latest answer - the same
  one its **Employer reply** column shows. The reply choices appear once tracking is on.

## Answering an employer

Press **Reply** on a mail in the *Replies from employers* card (an info request or an interview invitation is the usual reason).

1. **Claude writes a draft.** It reads that one mail (read-only, as above) and writes an answer in the mail's language, using only what is
   in your profile: name, contact details, work authorization, notice period, expected salary, your saved answers and the start of the
   resume. Anything it cannot know it marks as `[[like this]]` instead of guessing - for example `[[the times you are free]]`.
   It never promises an attachment: nothing can be attached from here.
2. **You read it and change it.** The text is yours. *Save draft* keeps it (it is also kept when you close the window, and opening the mail
   again shows it without another Claude run); *Write it again* asks Claude for a new one. A mail from a `no-reply` address is
   marked, because nobody will read an answer to it.
3. **Send sends exactly that text**, as a reply in the same Gmail conversation, from your own Gmail. Send is refused while a
   `[[placeholder]]` is left, while the box is empty, and if the same mail was answered in the last 30 seconds. Afterwards the mail shows
   *you replied* and the button becomes *Reply again*.

What keeps this safe:

* **Nothing is sent without your click on Send**, and drafting never has a sending tool: the draft run gets the same read-only tools as a
  scan, with the reply tool refused by name.
* **The send run is given one tool and nothing else** - the connector's `reply` tool (`GmailToolPolicy.IsReplyTool`: a Gmail tool named
  *reply*, not *reply all*, not *send*) - and no search, no reading, no files. The prompt carries your text as one JSON string and the mail's
  id, nothing from any mail.
* **"Sent" means Gmail said so.** The app follows the run's own tool call and the tool's result; what Claude says about it counts for
  nothing. If Gmail refused, you get its reason and your text stays as a draft. If the run ended without an answer after asking Gmail to
  send, it says so and tells you to look in the *Sent* folder before trying again, rather than offering a retry that could mail the
  employer twice. If Claude passed on something other than your text, or set recipients you did not ask for, you are told after the fact.
* A send is not cancelled by closing the page, so it is never left half done.
* Like everything here, it uses your Claude usage (one short run for the draft, one for the send) and the text goes through Claude
  (Anthropic).
