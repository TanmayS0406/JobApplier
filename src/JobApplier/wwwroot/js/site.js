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
                reflectHelpCount(Number(response.headers.get('X-Help-Count') ?? 0));
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
            signOut.disabled = !s.loggedIn;
            refreshPill(force);
        }
        function stopWaiting() { if (waiting) { clearInterval(waiting); waiting = null; } }

        async function post(url) {
            const r = await fetch(url, { method: 'POST', headers: { RequestVerificationToken: csrf } });
            return r.json();
        }

        signIn.addEventListener('click', async () => {
            const result = await post(account.dataset.signinUrl);
            if (!result.ok) { text.textContent = result.error; return; }
            text.textContent = 'Finish signing in in the browser and the console window that just opened...';
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
