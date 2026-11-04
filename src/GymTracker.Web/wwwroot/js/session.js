// Set logging that survives bad gym signal, plus the rest timer (SPEC sections 7 and 8).
//
// A set is written to localStorage before it is sent, so closing the app or losing signal
// mid-request never loses it. The queue is retried on reconnect, when the app comes back to
// the foreground, after any successful request and every 15 seconds. The server treats a
// repeated ClientId as the same set, so retries never duplicate.
(function () {
    "use strict";

    const queue = window.gymQueue("gym.pendingSets");
    const owner = document.documentElement.dataset.userId || "";
    const REST_KEY = "gym.restTimer.v2." + encodeURIComponent(owner);
    const RETRY_MS = 15000;

    // Set while a session page is shown (the "session" page module below); the queue itself runs on every page.
    let sessionId = null;
    let sessionActive = false;

    // ---- storage (private browsing or a full disk must not break logging) ----

    function read(key, fallback) {
        try {
            const raw = localStorage.getItem(key);
            return raw ? JSON.parse(raw) : fallback;
        } catch {
            return fallback;
        }
    }

    function write(key, value) {
        try {
            if (value === null) {
                localStorage.removeItem(key);
            } else {
                localStorage.setItem(key, JSON.stringify(value));
            }
        } catch {
            // Nothing more we can do; the in-flight request may still succeed.
        }
    }

    const loadQueue = queue.load;
    const saveQueue = queue.save;

    // ---- queue ----

    function rowFor(clientId) {
        return document.querySelector(`form.set-row[data-client-id="${clientId}"]`);
    }

    function markPending(form, item) {
        form.classList.add("pending");
        // Looks logged straight away; "Waiting for signal" only appears if the server hasn't answered soon.
        setTimeout(() => {
            if (form.isConnected && form.classList.contains("pending")) {
                form.classList.add("is-waiting");
            }
        }, navigator.onLine ? 1500 : 0);
        form.dataset.clientId = item.clientId;
        form.querySelector('input[name="clientId"]').value = item.clientId;
        form.querySelector('input[name="weightKg"]').value = item.weightKg;
        form.querySelector('input[name="reps"]').value = item.reps;
        form.querySelectorAll("input, button").forEach((el) => { el.disabled = true; });
    }

    function updateStatus() {
        const status = document.getElementById("queue-status");
        if (!status) {
            return;
        }

        const count = loadQueue().length;
        status.hidden = count === 0 && !queue.hasLegacy();
        status.textContent = queue.hasLegacy()
            ? t("Older pending changes have no account attached. They are kept on this phone but cannot be sent automatically.")
            : !queue.durable && count > 0
            ? t("This browser can't save pending changes. Keep this page open until they are sent.")
            : count === 1
            ? t("1 set is saved on this phone and will send when the connection is back.")
            : t("{0} sets are saved on this phone and will send when the connection is back.", count);
    }

    function toast(message) {
        if (window.gymToast) {
            window.gymToast(message);
        }
    }

    let sending = false;

    async function flush() {
        if (sending || !queue.active()) {
            return;
        }
        sending = true;
        try {
            for (const item of loadQueue()) {
                if (!queue.active()) {
                    break;
                }
                const result = await send(item);
                if (result === "stop") {
                    break;
                }
            }
        } finally {
            sending = false;
            updateStatus();
        }
    }

    function removeFromQueue(clientId) {
        saveQueue(loadQueue().filter((q) => q.clientId !== clientId));
    }

    // Returns "next" to carry on with the queue or "stop" to wait for the next retry.
    async function send(item) {
        const body = new URLSearchParams({
            clientId: item.clientId,
            sessionExerciseId: String(item.sessionExerciseId),
            weightKg: String(item.weightKg),
            reps: String(item.reps),
            isWarmup: item.isWarmup ? "true" : "false",
            completedAt: item.completedAt,
        });

        let response;
        try {
            response = await fetch("/train/sets", {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/x-www-form-urlencoded",
                    "RequestVerificationToken": window.gymToken(),
                    "HX-Request": "true",
                    ...queue.headers(),
                },
                body,
            });
        } catch {
            return "stop"; // offline or the request was cut off
        }

        if (response.ok) {
            const html = await response.text();
            removeFromQueue(item.clientId);
            const row = rowFor(item.clientId);
            if (row) {
                const holder = document.createElement("div");
                holder.innerHTML = html.trim();
                const fresh = holder.firstElementChild;
                if (fresh) {
                    row.replaceWith(fresh);
                    if (window.htmx) {
                        window.htmx.process(fresh);
                    }
                }
            }
            return "next";
        }

        if (response.status === 401) {
            if (!queue.durable) {
                // Navigating to login would destroy the only copy of a queue held in memory.
                toast(t("This session expired. Sign in again in another tab, then return here to send pending changes."));
                return "stop";
            }
            // Signed out. Keep the queue; it sends after logging back in.
            window.location.href = "/account/login?returnUrl=" + encodeURIComponent(window.location.pathname);
            return "stop";
        }

        if (response.status === 409) {
            if (queue.durable) {
                queue.pause();
                toast(t("Your sign-in changed. Reload this page before sending pending changes."));
            } else {
                toast(t("Your sign-in changed. Sign back into this account in another tab, then return here to send pending changes."));
            }
            return "stop";
        }

        if (response.status === 400 || response.status === 404) {
            // Will never succeed: invalid values, or the exercise was removed. Drop it and say so.
            removeFromQueue(item.clientId);
            const row = rowFor(item.clientId);
            if (row) {
                row.classList.remove("pending");
                row.querySelectorAll("input, button").forEach((el) => { el.disabled = false; });
            }
            row && row.classList.remove("is-waiting");
            toast(response.status === 400 ? (await response.text()) || t("That set was not valid.") : t("That set's exercise no longer exists."));
            return "next";
        }

        return "stop"; // server error: try again later
    }

    // Suggested values are placeholders until the set is logged; an untouched field takes the suggestion.
    // This runs on the tap (and on Enter, which clicks the submit button) because the browser checks
    // required fields before the submit event fires.
    function fillSuggestions(form) {
        form.querySelectorAll("input[data-suggested]").forEach((input) => {
            if (input.value === "" && input.dataset.suggested) {
                input.value = input.dataset.suggested;
            }
        });
    }

    document.addEventListener("click", (event) => {
        const button = event.target.closest("form[data-log-set] button[type=submit]");
        if (button) {
            fillSuggestions(button.form);
        }
    }, true);

    function logSet(form) {
        fillSuggestions(form);
        if (!form.reportValidity()) {
            return;
        }
        form.classList.add("just-done");

        const item = {
            clientId: form.querySelector('input[name="clientId"]').value,
            sessionId,
            sessionExerciseId: Number(form.querySelector('input[name="sessionExerciseId"]').value),
            // A comma typed on the Bulgarian keypad is stored with a dot, like every number in the app.
            weightKg: form.querySelector('input[name="weightKg"]').value.trim().replace(",", "."),
            reps: form.querySelector('input[name="reps"]').value,
            isWarmup: form.querySelector('input[name="isWarmup"]').value === "true",
            completedAt: new Date().toISOString(),
        };

        const pending = loadQueue();
        if (!pending.some((q) => q.clientId === item.clientId)) {
            pending.push(item);
            saveQueue(pending);
        }

        markPending(form, item);
        updateStatus();

        if (!item.isWarmup) {
            carryOver(form, item);
            const card = form.closest(".exercise");
            startRest(card ? Number(card.dataset.rest) : 120, card);
        }

        flush();
    }

    // Without a progression suggestion, the rows after a logged set start from it (SPEC 7): an untouched row then
    // logs the same weight and reps. Rows filled this way (data-carried) follow the latest set; real suggestions stay.
    function carryOver(form, item) {
        for (let row = form.nextElementSibling; row; row = row.nextElementSibling) {
            if (!row.matches("form[data-log-set]:not(.pending)")) {
                continue;
            }
            const kg = row.querySelector('input[name="weightKg"]');
            if (kg.dataset.suggested && row.dataset.carried !== "true") {
                continue;
            }
            row.dataset.carried = "true";
            [[kg, item.weightKg], [row.querySelector('input[name="reps"]'), item.reps]].forEach(([input, value]) => {
                input.placeholder = value;
                input.dataset.suggested = value;
            });
        }
    }

    // Put sets still waiting in the queue back on the page after a reload or relaunch.
    function restorePending() {
        if (!sessionId) {
            return;
        }

        const queue = loadQueue();
        const kept = [];
        for (const item of queue) {
            const existing = rowFor(item.clientId);
            if (existing && existing.classList.contains("done")) {
                continue; // already saved on the server; the response was just lost
            }
            kept.push(item);
            if (item.sessionId !== sessionId || existing) {
                continue;
            }

            const container = document.getElementById((item.isWarmup ? "warmups-" : "rows-") + item.sessionExerciseId);
            const blank = container && container.querySelector("form[data-log-set]:not(.pending)");
            if (blank) {
                markPending(blank, item);
            }
        }
        saveQueue(kept);
        updateStatus();
    }

    document.addEventListener("submit", (event) => {
        const form = event.target.closest("form[data-log-set]");
        if (form) {
            event.preventDefault();
            unlockAudio();
            keepAwake();
            logSet(form);
        }
    });

    // ---- row numbers and values typed into rows not logged yet (SPEC 7 and 8) ----
    // The server numbers a whole card; a row that arrives alone (logged, deleted, added, removed) is put back in order here.
    // What is typed into a row before it is logged is kept on the phone by the row's place in its card, so a reload or
    // a page swap (Move up, Swap) puts it back.

    const DRAFTS_KEY = "gym.setDrafts.v2." + encodeURIComponent(owner);
    const DRAFT_DAYS = 2;

    function renumber(rows) {
        Array.from(rows.children).forEach((row, i) => {
            const n = row.querySelector(".set-n");
            if (n) {
                n.textContent = String(i + 1);
            }
        });
    }

    function draftKey(rows, index) {
        return `${sessionId}:${rows.id}:${index}`;
    }

    // Rewrites one card's drafts from what is on screen, so they follow the rows when one is removed.
    function saveDrafts(rows) {
        if (!sessionId || !rows.id) {
            return;
        }
        const drafts = read(DRAFTS_KEY, {});
        const prefix = `${sessionId}:${rows.id}:`;
        Object.keys(drafts).filter((k) => k.startsWith(prefix)).forEach((k) => { delete drafts[k]; });
        Array.from(rows.children).forEach((row, i) => {
            if (!row.matches("form[data-log-set]:not(.pending)")) {
                return;
            }
            const kg = row.querySelector('input[name="weightKg"]').value;
            const reps = row.querySelector('input[name="reps"]').value;
            if (kg !== "" || reps !== "") {
                drafts[draftKey(rows, i)] = { kg, reps, at: Date.now() };
            }
        });
        write(DRAFTS_KEY, Object.keys(drafts).length ? drafts : null);
    }

    function restoreDrafts() {
        const drafts = read(DRAFTS_KEY, {});
        const cutoff = Date.now() - DRAFT_DAYS * 86400000;
        Object.keys(drafts).filter((k) => !(drafts[k].at > cutoff)).forEach((k) => { delete drafts[k]; });
        write(DRAFTS_KEY, Object.keys(drafts).length ? drafts : null);
        document.querySelectorAll(".rows.working, .rows.warmups").forEach((rows) => {
            Array.from(rows.children).forEach((row, i) => {
                const draft = drafts[draftKey(rows, i)];
                if (!draft || !row.matches("form[data-log-set]:not(.pending)")) {
                    return;
                }
                const kg = row.querySelector('input[name="weightKg"]');
                const reps = row.querySelector('input[name="reps"]');
                kg.value = kg.value || draft.kg;
                reps.value = reps.value || draft.reps;
            });
        });
    }

    document.addEventListener("input", (event) => {
        const row = event.target.closest("form[data-log-set]");
        if (row && row.parentElement) {
            saveDrafts(row.parentElement);
        }
    });

    // Rows come and go through htmx swaps whose events fire on elements that may already be gone, so watch the cards.
    let rowsObserver = null;
    function watchRows() {
        rowsObserver = new MutationObserver((records) => {
            const changed = new Set(records.map((r) => r.target.closest && r.target.closest(".rows")).filter(Boolean));
            changed.forEach((rows) => {
                if (rows.classList.contains("working")) {
                    renumber(rows);
                }
                saveDrafts(rows);
            });
        });
        document.querySelectorAll(".rows").forEach((rows) => rowsObserver.observe(rows, { childList: true }));
    }

    window.addEventListener("online", flush);
    window.addEventListener("pageshow", flush);
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            flush();
            keepAwake();
            ensureTicking();
        }
    });
    document.body.addEventListener("htmx:afterRequest", (event) => {
        if (event.detail.successful) {
            flush();
        }
    });
    setInterval(() => {
        if (loadQueue().length) {
            flush();
        }
    }, RETRY_MS);

    // ---- rest timer ----
    // Remaining time is always computed from the stored end time: iOS pauses timers in the background.

    let audio = null;

    function unlockAudio() {
        // iOS only lets audio start from a tap, so create and resume the context on the check button.
        try {
            audio = audio || new (window.AudioContext || window.webkitAudioContext)();
            if (audio.state === "suspended") {
                audio.resume();
            }
        } catch {
            audio = null;
        }
    }

    function beep() {
        if (!audio) {
            return;
        }
        const start = audio.currentTime;
        [0, 0.25, 0.5].forEach((offset) => {
            const osc = audio.createOscillator();
            const gain = audio.createGain();
            osc.frequency.value = 880;
            gain.gain.setValueAtTime(0.0001, start + offset);
            gain.gain.exponentialRampToValueAtTime(0.4, start + offset + 0.02);
            gain.gain.exponentialRampToValueAtTime(0.0001, start + offset + 0.18);
            osc.connect(gain).connect(audio.destination);
            osc.start(start + offset);
            osc.stop(start + offset + 0.2);
        });
        if (navigator.vibrate) {
            navigator.vibrate([200, 100, 200]); // Android only; iOS has no vibration API
        }
    }

    // `card` is the exercise just logged; Next starts looking from it.
    function startRest(seconds, card) {
        if (!sessionActive || !seconds) {
            return;
        }
        write(REST_KEY, { sessionId, end: Date.now() + seconds * 1000, total: seconds, beeped: false, exercise: card ? card.id : null });
        showNext();
        ensureTicking();
    }

    // Two loops run only while a rest timer is set on the session page: animation frames for a smooth ring while
    // the page is visible, and a 250 ms interval that keeps the logic (the beep) going while it is not.
    let tickTimer = null;
    let frameId = 0;

    function ensureTicking() {
        if (!tick() || tickTimer) {
            return;
        }
        tickTimer = setInterval(() => {
            if (document.visibilityState !== "visible" && !tick()) {
                stopTicking();
            }
        }, 250);
        frameId = requestAnimationFrame(frame);
    }

    function frame() {
        if (document.visibilityState === "visible" && !tick()) {
            stopTicking();
            return;
        }
        frameId = requestAnimationFrame(frame);
    }

    function stopTicking() {
        clearInterval(tickTimer);
        tickTimer = null;
        cancelAnimationFrame(frameId);
    }

    // Updates the timer on screen; returns whether a rest is still running.
    function tick() {
        const timer = document.getElementById("rest-timer");
        if (!timer) {
            return false;
        }

        const state = read(REST_KEY, null);
        if (!state || state.sessionId !== sessionId) {
            timer.hidden = true;
            return false;
        }

        const leftMs = Math.max(0, state.end - Date.now());
        const remaining = Math.ceil(leftMs / 1000);
        const text = `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, "0")}`;
        if (timer.hidden) {
            timer.hidden = false;
            showNext();
        }
        document.getElementById("rest-time").textContent = text;
        document.querySelectorAll("[data-rest-time]").forEach((el) => { el.textContent = text; });
        // The ring empties as the rest runs out (circles use pathLength="1").
        const left = Math.min(1, leftMs / (state.total * 1000));
        document.querySelectorAll("[data-rest-ring]").forEach((ring) => {
            ring.style.strokeDasharray = "1 1";
            ring.style.strokeDashoffset = String(1 - left);
        });
        timer.classList.toggle("done", remaining === 0);
        const sheet = document.querySelector(".rest-sheet");
        if (sheet) {
            sheet.classList.toggle("done", remaining === 0);
        }

        if (remaining === 0 && !state.beeped) {
            state.beeped = true;
            write(REST_KEY, state);
            beep();
            const live = document.getElementById("rest-live");
            if (live) {
                live.textContent = t("Rest over");
            }
        }

        // Clear a finished timer after a few seconds so it doesn't linger.
        if (remaining === 0 && Date.now() - state.end > 4000) {
            write(REST_KEY, null);
            timer.hidden = true;
            if (window.gymSheet) {
                window.gymSheet.hide();
            }
            return false;
        }

        return true;
    }

    // The next working set not logged yet: in the exercise just logged, else the first exercise after it with one left,
    // wrapping round to the top.
    function nextRow() {
        const state = read(REST_KEY, null);
        const cards = Array.from(document.querySelectorAll(".exercise"));
        const from = state && state.exercise ? cards.findIndex((c) => c.id === state.exercise) : -1;
        const order = from < 0 ? cards : cards.slice(from).concat(cards.slice(0, from));
        for (const card of order) {
            const row = card.querySelector(".rows.working form[data-log-set]:not(.pending)");
            if (row) {
                return row;
            }
        }
        return null;
    }

    // What comes after the rest: the next set (above), with its suggested weight and reps.
    function showNext() {
        const next = nextRow();
        const card = next && next.closest(".exercise");
        const name = card ? card.dataset.name : "";
        const kg = next ? next.querySelector('input[name="weightKg"]') : null;
        const reps = next ? next.querySelector('input[name="reps"]') : null;
        const kgValue = kg && (kg.value || kg.dataset.suggested);
        const repsValue = reps && (reps.value || reps.dataset.suggested);
        const short = document.querySelector("[data-rest-next-short]");
        if (short) {
            short.textContent = name ? t("Next: {0}", name) : t("Rest");
        }
        const long = document.querySelector("[data-rest-next]");
        if (long) {
            long.textContent = !name ? "" : kgValue && repsValue
                ? t("Next: {0}", name) + " · " + t("{0} kg × {1}", kgValue, repsValue)
                : t("Next: {0}", name);
        }
    }

    function adjustRest(byMs) {
        const state = read(REST_KEY, null);
        if (!state) {
            return;
        }
        state.end = Math.max(Date.now(), state.end) + byMs;
        state.total = Math.max(state.total, Math.ceil((state.end - Date.now()) / 1000));
        state.beeped = false;
        write(REST_KEY, state);
        ensureTicking();
    }

    document.addEventListener("click", (event) => {
        if (event.target.closest("#rest-add, [data-rest-add]")) {
            unlockAudio();
            adjustRest(30000);
        } else if (event.target.closest("#rest-skip, [data-rest-skip]")) {
            write(REST_KEY, null);
            stopTicking();
            tick();
            if (window.gymSheet) {
                window.gymSheet.hide();
            }
        }
    });

    // ---- screen wake lock: keep the phone awake between sets during a workout (iOS 18.4+) ----

    let wakeLock = null;

    async function keepAwake() {
        if (!sessionActive || !("wakeLock" in navigator) || (wakeLock && !wakeLock.released)) {
            return;
        }
        try {
            wakeLock = await navigator.wakeLock.request("screen");
        } catch {
            wakeLock = null;
        }
    }

    // ---- start ----

    if (navigator.storage && navigator.storage.persist) {
        navigator.storage.persist().catch(() => {});
    }

    flush();

    // The session page: merge pending sets into it, keep the phone awake and run the rest timer.
    // Leaving the page (soft navigation) stops the timer loops and releases the wake lock.
    window.gymModule("session", (el) => {
        sessionId = Number(el.dataset.sessionId);
        sessionActive = el.dataset.active === "true";
        restorePending();
        restoreDrafts();
        watchRows();
        flush();
        keepAwake();
        ensureTicking();
        return () => {
            if (rowsObserver) {
                rowsObserver.disconnect();
                rowsObserver = null;
            }
            sessionId = null;
            sessionActive = false;
            stopTicking();
            if (wakeLock) {
                wakeLock.release().catch(() => {});
                wakeLock = null;
            }
        };
    });
})();
