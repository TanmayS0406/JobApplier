// No inline scripts are allowed (Content-Security-Policy), so all behaviour lives here and is driven by data-* attributes.
(() => {
    'use strict';

    const csrf = document.querySelector('meta[name="csrf"]')?.content ?? '';
    const baseTitle = document.title;
    let lastHelpCount = 0;

    // ---------- polling: [data-poll-url] ----------
    function startPolling(el) {
        const url = el.dataset.pollUrl;
        const every = Number(el.dataset.pollMs) || 2500;
        let busy = false;

        // Never replace the DOM while the user is typing an answer inside it.
        const userIsTyping = () => {
            const active = document.activeElement;
            return !!active && el.contains(active) && /^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName);
        };

        async function tick() {
            if (busy || document.hidden || userIsTyping()) return;

            busy = true;
            try {
                const response = await fetch(url, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' });
                if (!response.ok) return;
                const html = await response.text();
                if (userIsTyping()) return; // started typing while the request was in flight
                el.innerHTML = html;
                // Only the live job list reports a help count; other polled areas (the replies card) must not reset the tab title.
                const helpCount = response.headers.get('X-Help-Count');
                if (helpCount !== null) reflectHelpCount(Number(helpCount));
                if (el.querySelector('[data-terminal="true"]')) clearInterval(timer);
            } catch {
                // server restarting or offline: try again on the next tick
            } finally {
                busy = false;
            }
        }

        const timer = setInterval(tick, every);
    }
    document.querySelectorAll('[data-poll-url]').forEach(startPolling);

    // The tab title and a short beep make "this job needs you" hard to miss while another window is in front.
    function reflectHelpCount(count) {
        document.title = count > 0 ? `(${count}) Needs you - JobApplier` : baseTitle;
        if (count > lastHelpCount) beep();
        lastHelpCount = count;
    }
    function beep() {
        try {
            const ctx = new (window.AudioContext || window.webkitAudioContext)();
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            osc.frequency.value = 880;
            gain.gain.value = 0.05;
            osc.connect(gain).connect(ctx.destination);
            osc.start();
            osc.stop(ctx.currentTime + 0.18);
        } catch { /* audio not available */ }
    }
    reflectHelpCount(document.querySelectorAll('.needs-you .help-card').length);

    // ---------- answering a help request: [data-help-form] ----------
    document.addEventListener('submit', async (event) => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement)) return;

        if (form.dataset.confirm && !confirm(form.dataset.confirm)) {
            event.preventDefault();
            return;
        }
        if (form.dataset.busy) {
            showBusy(form.dataset.busy);
            return; // normal submit
        }
        if (!form.hasAttribute('data-help-form')) return;

        event.preventDefault();
        const submitter = event.submitter;
        const data = new FormData(form, submitter);
        if (submitter?.value === 'answer' && !String(data.get('text') ?? '').trim()) {
            form.querySelector('input[name="text"]')?.focus();
            return;
        }

        form.querySelectorAll('button').forEach(b => (b.disabled = true));
        try {
            // getAttribute, not form.action: the buttons are named "action", and a control named like a form property
            // replaces it, so form.action is the list of those buttons ("/[object RadioNodeList]", a 404).
            const response = await fetch(form.getAttribute('action'), { method: 'POST', body: data, headers: { 'X-Requested-With': 'fetch' } });
            if (!response.ok) alert((await response.text()) || `That did not work (HTTP ${response.status}).`);
        } catch {
            alert('That did not work: the app could not be reached. Is it still running?');
        } finally {
            const area = form.closest('[data-poll-url]');
            if (area) {
                const r = await fetch(area.dataset.pollUrl, { cache: 'no-store' });
                if (r.ok) area.innerHTML = await r.text();
            } else {
                location.reload();
            }
        }
    });

    function showBusy(text) {
        const box = document.getElementById('busy');
        document.getElementById('busy-text').textContent = text;
        box.hidden = false;
    }

    // ---------- AI provider status (Claude Code or Gemini CLI) ----------
    // Both providers answer with { cliFound, loggedIn, detail, plan }, so one routine serves the header pill and both account cards.
    async function fetchStatus(url, force) {
        try {
            const r = await fetch(`${url}?force=${force}`, { cache: 'no-store' });
            return await r.json();
        } catch { return null; }
    }

    const pill = document.getElementById('provider-pill');
    async function refreshPill(force = false) {
        if (!pill) return null;
        const s = await fetchStatus(pill.dataset.statusUrl, force);
        if (!s) return null;
        const name = pill.dataset.provider || 'AI';
        pill.classList.remove('pill-ok', 'pill-bad', 'pill-unknown');
        if (!s.cliFound) { pill.textContent = name + ' CLI not found'; pill.classList.add('pill-bad'); }
        else if (!s.loggedIn) { pill.textContent = name + ': not signed in'; pill.classList.add('pill-bad'); }
        else { pill.textContent = name + ': ' + (s.detail || 'signed in') + (s.plan ? ' (' + s.plan + ')' : ''); pill.classList.add('pill-ok'); }
        return s;
    }
    refreshPill();
    setInterval(refreshPill, 30000);

    // ---------- Settings: sign in / out, one card per provider ----------
    function setupAccountCard(prefix) {
        const account = document.getElementById(prefix + '-account');
        if (!account) return;
        const text = document.getElementById(prefix + '-account-text');
        const signIn = document.getElementById(prefix + '-signin');
        const signOut = document.getElementById(prefix + '-signout');
        let waiting = null;

        async function refresh(force) {
            const s = await fetchStatus(account.dataset.statusUrl, force);
            if (!s) return;
            if (!s.cliFound) text.textContent = account.dataset.installHint;
            else if (s.loggedIn) { text.textContent = 'Signed in' + (s.detail ? ' as ' + s.detail : '') + (s.plan ? ' - ' + s.plan + ' plan' : ''); stopWaiting(); }
            else if (!waiting) text.textContent = 'Not signed in.';
            signIn.disabled = !s.cliFound || s.loggedIn;
            // Gemini says where the sign-in comes from; only a Google login can be "signed out" (a key has its own Remove button).
            signOut.disabled = s.source !== undefined ? s.source !== 'google' : !s.loggedIn;
            const install = document.getElementById(prefix + '-install');
            if (install) install.hidden = s.cliFound;
            const removeKey = document.getElementById(prefix + '-key-remove');
            if (removeKey) removeKey.disabled = s.source !== 'api-key-file';
            refreshPill(force);
        }
        account.addEventListener('account-refresh', () => refresh(true));
        function stopWaiting() { if (waiting) { clearInterval(waiting); waiting = null; } }

        async function post(url) {
            const r = await fetch(url, { method: 'POST', headers: { RequestVerificationToken: csrf } });
            return r.json();
        }

        signIn.addEventListener('click', async () => {
            const result = await post(account.dataset.signinUrl);
            if (!result.ok) { text.textContent = result.error; return; }
            text.textContent = account.dataset.provider === 'Gemini'
                ? "In the window that just opened, press Enter on 'Login with Google', then choose your account in the browser. This page updates by itself."
                : 'Finish signing in in the browser and the console window that just opened...';
            waiting = setInterval(() => refresh(true), 3000);
            setTimeout(stopWaiting, 10 * 60 * 1000);
        });
        signOut.addEventListener('click', async () => {
            const result = await post(account.dataset.signoutUrl);
            if (result && result.error) text.textContent = result.error;
            refresh(true);
        });
        refresh(false);
    }
    setupAccountCard('claude');
    setupAccountCard('gemini');

    // ---------- Gemini: install the CLI, save / remove an API key ----------
    (function setupGeminiExtras() {
        const account = document.getElementById('gemini-account');
        if (!account) return;
        const $ = id => document.getElementById(id);
        const refresh = () => account.dispatchEvent(new Event('account-refresh'));
        const post = async (url, body) => (await fetch(url, { method: 'POST', headers: { RequestVerificationToken: csrf }, body })).json();

        $('gemini-install-btn').addEventListener('click', async () => {
            const button = $('gemini-install-btn'), msg = $('gemini-install-msg');
            button.disabled = true;
            msg.textContent = 'Opening the install window...';
            try {
                const result = await post(account.dataset.installUrl);
                if (!result.ok) { msg.textContent = result.error; button.disabled = false; return; }
            } catch { msg.textContent = 'Could not reach the app.'; button.disabled = false; return; }

            msg.textContent = 'Installing - this page updates by itself when it is done.';
            const timer = setInterval(async () => {
                const status = await fetchStatus(account.dataset.statusUrl, true);
                if (status && status.cliFound) { clearInterval(timer); msg.textContent = 'Installed.'; refresh(); }
            }, 3000);
            setTimeout(() => { clearInterval(timer); button.disabled = false; }, 10 * 60 * 1000);
        });

        $('gemini-key-save').addEventListener('click', async () => {
            const input = $('gemini-key'), msg = $('gemini-key-msg'), button = $('gemini-key-save');
            const key = input.value.trim();
            if (!key) { msg.textContent = 'Paste your API key first.'; input.focus(); return; }

            button.disabled = true;
            msg.textContent = 'Checking the key with Google...';
            try {
                const data = new FormData();
                data.append('key', key);
                const result = await post(account.dataset.savekeyUrl, data);
                msg.textContent = result.message;
                if (result.ok) input.value = '';
            } catch { msg.textContent = 'Could not reach the app.'; }
            button.disabled = false;
            refresh();
        });

        $('gemini-key-remove').addEventListener('click', async () => {
            const result = await post(account.dataset.removekeyUrl);
            $('gemini-key-msg').textContent = result.message || 'The saved key was removed.';
            refresh();
        });
    })();

    // ---------- Gmail replies: Settings card (Claude reads the mailbox through the Gmail connector of the user's Claude account) ----------
    (function setupGmail() {
        const account = document.getElementById('gmail-account');
        if (!account) return;
        const $ = id => document.getElementById(id);
        const post = async (url, body) => (await fetch(url, { method: 'POST', headers: { RequestVerificationToken: csrf }, body })).json();
        const when = iso => (iso ? new Date(iso).toLocaleString() : '');
        let status = null;
        let waiting = null;   // polls while the user finishes connecting Gmail in the Claude window
        const stopWaiting = () => { if (waiting) { clearInterval(waiting); waiting = null; } };

        // force = ask a live Claude session about the connector again (a few seconds); otherwise a recent answer is used.
        async function refresh(force) {
            try {
                status = await (await fetch(`${account.dataset.statusUrl}?force=${force ? 'true' : 'false'}`, { cache: 'no-store' })).json();
            } catch { return null; }
            const s = status;
            const ready = s.state === 'ready';

            let text;
            if (s.checking) {
                text = 'Claude is looking through your mailbox now...';
            } else if (ready) {
                text = 'Gmail is connected through your Claude account. ';
                text += s.enabled
                    ? 'Tracking is on' + (s.autoHours > 0 ? ` (checked by itself every ${s.autoHours} h)` : ' (only when you press the button)')
                        + (s.lastCheckedUtc ? '; last checked ' + when(s.lastCheckedUtc) : '; not checked yet') + `. ${s.replyCount} repl${s.replyCount === 1 ? 'y' : 'ies'} found so far.`
                    : 'Tracking is off: press Start tracking replies in step 2.';
                if (s.enabled && s.lastError) text += ' The last check failed: ' + s.lastError;
            } else {
                text = s.detail;
            }
            $('gmail-text').textContent = text;

            const chip = $('gmail-chip');
            chip.hidden = s.state === 'unknown';
            chip.textContent = ready ? 'connected' : 'not connected';
            chip.className = 'chip ' + (ready ? 'chip-good' : 'chip-gap');

            $('gmail-connect').disabled = ready || s.state === 'climissing';
            $('gmail-verify').disabled = !!s.checking;
            $('gmail-enable').disabled = !ready || s.enabled || !!s.checking;
            $('gmail-check').disabled = !ready || !!s.checking;
            $('gmail-rescan').disabled = !ready || !!s.checking;
            $('gmail-disable').disabled = !s.enabled;
            $('gmail-clear').disabled = s.replyCount === 0;
            return s;
        }

        // A busy button shows what it is doing; the message line keeps the result.
        async function run(button, message, work) {
            const msg = $('gmail-msg');
            button.disabled = true;
            msg.textContent = message;
            try {
                await work(msg);
            } catch {
                msg.textContent = 'Could not reach the app. Is it still running?';
            }
            await refresh(false);
        }

        $('gmail-connect').addEventListener('click', async () => {
            const msg = $('gmail-connect-msg');
            $('gmail-connect').disabled = true;
            try {
                const result = await post(account.dataset.connectUrl);
                if (!result.ok) { msg.textContent = result.error; $('gmail-connect').disabled = false; return; }
            } catch { msg.textContent = 'Could not reach the app.'; $('gmail-connect').disabled = false; return; }

            msg.textContent = 'A Claude window opened. Choose claude.ai Gmail, then Authenticate (or type /mcp). This page notices by itself when it works.';
            stopWaiting();
            let tries = 0;
            waiting = setInterval(async () => {
                const s = await refresh(true);
                if (s && s.state === 'ready') { stopWaiting(); msg.textContent = 'Gmail is connected. You can close the Claude window.'; }
                else if (++tries >= 60) { stopWaiting(); msg.textContent = 'Still not connected. Finish the sign-in, then press Check connection.'; }
            }, 10000);
        });

        $('gmail-verify').addEventListener('click', async () => {
            const msg = $('gmail-connect-msg');
            $('gmail-verify').disabled = true;
            msg.textContent = 'Asking Claude which connectors it can use...';
            const s = await refresh(true);
            msg.textContent = s ? s.detail : 'Could not reach the app.';
        });

        $('gmail-enable').addEventListener('click', () => run($('gmail-enable'), 'Starting...', async (msg) => {
            const started = await post(account.dataset.enableUrl);
            msg.textContent = started.message;
            if (!started.ok) return;   // Claude cannot use Gmail yet: nothing was turned on, the message says what is missing
            // The first check runs in the background; follow it until it is done.
            const timer = setInterval(async () => {
                const s = await refresh(false);
                if (s && !s.checking) { clearInterval(timer); $('gmail-msg').textContent = s.lastError ? 'The first check failed: ' + s.lastError : 'The first check is done.'; }
            }, 5000);
            setTimeout(() => clearInterval(timer), 15 * 60 * 1000);
        }));

        const check = (rescan) => async (msg) => {
            const data = new FormData();
            data.append('rescan', rescan ? 'true' : 'false');
            const result = await post(account.dataset.checkUrl, data);
            msg.textContent = result.message;
        };
        $('gmail-check').addEventListener('click', () => run($('gmail-check'), 'Claude is looking through your mailbox... this can take a minute or two.', check(false)));
        $('gmail-rescan').addEventListener('click', () => run($('gmail-rescan'), 'Claude is reading the mailbox back to your first application...', check(true)));

        $('gmail-disable').addEventListener('click', () => run($('gmail-disable'), 'Stopping...', async (msg) => {
            msg.textContent = (await post(account.dataset.disableUrl)).message;
        }));
        $('gmail-clear').addEventListener('click', () => {
            if (!confirm('Delete every saved reply? Your mailbox is not touched; the next check finds the replies again.')) return;
            return run($('gmail-clear'), 'Deleting...', async (msg) => {
                msg.textContent = (await post(account.dataset.clearUrl)).message;
            });
        });

        refresh(false);
        setInterval(() => { if (!document.hidden && status && status.checking) refresh(false); }, 5000);
    })();

    // ---------- Gmail: replies card on the dashboard (it is re-rendered by polling, so clicks are handled on the document) ----------
    document.addEventListener('click', async (event) => {
        const area = document.getElementById('replies');
        if (!area || !(event.target instanceof Element)) return;

        const redraw = async () => {
            const r = await fetch(area.dataset.pollUrl, { cache: 'no-store' });
            if (r.ok) area.innerHTML = await r.text();
        };

        // Follows the first check after tracking was started: redraws the card until Claude is done with it.
        const followChecking = () => {
            let seen = false, ticks = 0;
            const timer = setInterval(async () => {
                await redraw();
                ticks++;
                const checking = !!area.querySelector('[data-checking="true"]');
                if (checking) seen = true;
                if ((seen && !checking) || (!seen && ticks >= 5) || ticks > 240) clearInterval(timer);
            }, 3000);
        };

        const enableButton = event.target.closest('[data-gmail-enable]');
        if (enableButton && area.contains(enableButton)) {
            enableButton.disabled = true;
            const msg = area.querySelector('[data-gmail-msg]');
            if (msg) msg.textContent = 'Checking that Claude can reach your Gmail...';
            let result = null;
            try {
                result = await (await fetch(area.dataset.enableUrl, { method: 'POST', headers: { RequestVerificationToken: csrf } })).json();
            } catch { /* reported below */ }
            if (!result || !result.ok) {
                enableButton.disabled = false;
                if (msg) msg.textContent = result ? result.message : 'Could not reach the app. Is it still running?';
                return;
            }
            await redraw();
            followChecking();
            return;
        }

        const checkButton = event.target.closest('[data-gmail-check]');
        if (checkButton && area.contains(checkButton)) {
            checkButton.disabled = true;
            checkButton.textContent = 'Checking...';
            try {
                await fetch(area.dataset.checkUrl, { method: 'POST', headers: { RequestVerificationToken: csrf } });
            } catch { /* the card shows the state again below */ }
            await redraw();
            return;
        }

        const dismissButton = event.target.closest('[data-reply-dismiss]');
        if (dismissButton && area.contains(dismissButton)) {
            dismissButton.disabled = true;
            try {
                await fetch(`${area.dataset.dismissUrl}/${encodeURIComponent(dismissButton.dataset.replyDismiss)}`, { method: 'POST', headers: { RequestVerificationToken: csrf } });
            } catch { /* redrawn below */ }
            await redraw();
        }
    });

    // ---------- Upload: drag & drop, paste, previews ----------
    const dropzone = document.getElementById('dropzone');
    if (dropzone) {
        const input = document.getElementById('screenshots');
        const previews = document.getElementById('previews');
        const start = document.getElementById('start-jobs');
        let files = [];

        function sync() {
            const dt = new DataTransfer();
            files.forEach(f => dt.items.add(f));
            input.files = dt.files;
            previews.replaceChildren();
            files.forEach((file, index) => {
                const box = document.createElement('div');
                box.className = 'preview';
                const img = document.createElement('img');
                img.src = URL.createObjectURL(file);
                img.alt = '';
                const label = document.createElement('span');
                label.textContent = file.name;
                const remove = document.createElement('button');
                remove.type = 'button';
                remove.textContent = 'x';
                remove.title = 'Remove';
                remove.addEventListener('click', () => { files.splice(index, 1); sync(); });
                box.append(img, label, remove);
                previews.append(box);
            });
            start.disabled = files.length === 0;
            start.textContent = files.length === 0 ? 'Start jobs' : `Start ${files.length} screenshot${files.length === 1 ? '' : 's'}`;
        }
        function add(list) {
            for (const f of list) if (f.type.startsWith('image/')) files.push(f);
            sync();
        }

        document.getElementById('pick-files').addEventListener('click', () => input.click());
        input.addEventListener('change', () => { files = files.concat(Array.from(input.files)); sync(); });
        ['dragenter', 'dragover'].forEach(n => dropzone.addEventListener(n, e => { e.preventDefault(); dropzone.classList.add('drag'); }));
        ['dragleave', 'drop'].forEach(n => dropzone.addEventListener(n, e => { e.preventDefault(); dropzone.classList.remove('drag'); }));
        dropzone.addEventListener('drop', e => add(e.dataTransfer.files));
        document.addEventListener('paste', e => { if (e.clipboardData?.files?.length) add(e.clipboardData.files); });
        document.getElementById('upload-form').addEventListener('submit', () => showBusy('Uploading...'));
        sync();
    }
})();
