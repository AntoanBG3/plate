// Set logging that survives bad gym signal, plus the rest timer (SPEC sections 7 and 8).
//
// A set is written to localStorage before it is sent, so closing the app or losing signal
// mid-request never loses it. The queue is retried on reconnect, when the app comes back to
// the foreground, after any successful request and every 15 seconds. The server treats a
// repeated ClientId as the same set, so retries never duplicate.
(function () {
    "use strict";

    const QUEUE_KEY = "gym.pendingSets.v1";
    const REST_KEY = "gym.restTimer.v1";
    const RETRY_MS = 15000;

    const sessionEl = document.getElementById("session");
    const sessionId = sessionEl ? Number(sessionEl.dataset.sessionId) : null;
    const sessionActive = sessionEl ? sessionEl.dataset.active === "true" : false;

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

    const loadQueue = () => read(QUEUE_KEY, []);
    const saveQueue = (queue) => write(QUEUE_KEY, queue.length ? queue : null);

    function antiforgeryToken() {
        // Read at send time: a set queued before a reload must use the current page's token.
        try {
            return JSON.parse(document.body.getAttribute("hx-headers") || "{}").RequestVerificationToken || "";
        } catch {
            return "";
        }
    }

    // ---- queue ----

    function rowFor(clientId) {
        return document.querySelector(`form.set-row[data-client-id="${clientId}"]`);
    }

    function markPending(form, item) {
        form.classList.add("pending");
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
        status.hidden = count === 0;
        status.textContent = count === 1
            ? "1 set is saved on this phone and will send when the connection is back."
            : `${count} sets are saved on this phone and will send when the connection is back.`;
    }

    function toast(message) {
        const el = document.getElementById("toast");
        if (!el) {
            return;
        }
        el.textContent = message;
        el.hidden = false;
        setTimeout(() => { el.hidden = true; }, 5000);
    }

    let sending = false;

    async function flush() {
        if (sending) {
            return;
        }
        sending = true;
        try {
            for (const item of loadQueue()) {
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
                    "RequestVerificationToken": antiforgeryToken(),
                    "HX-Request": "true",
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
            // Signed out. Keep the queue; it sends after logging back in.
            window.location.href = "/account/login?returnUrl=" + encodeURIComponent(window.location.pathname);
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
            toast(response.status === 400 ? (await response.text()) || "That set was not valid." : "That set's exercise no longer exists.");
            return "next";
        }

        return "stop"; // server error: try again later
    }

    function logSet(form) {
        if (!form.reportValidity()) {
            return;
        }

        const item = {
            clientId: form.querySelector('input[name="clientId"]').value,
            sessionId,
            sessionExerciseId: Number(form.querySelector('input[name="sessionExerciseId"]').value),
            weightKg: form.querySelector('input[name="weightKg"]').value,
            reps: form.querySelector('input[name="reps"]').value,
            isWarmup: form.querySelector('input[name="isWarmup"]').value === "true",
            completedAt: new Date().toISOString(),
        };

        const queue = loadQueue();
        if (!queue.some((q) => q.clientId === item.clientId)) {
            queue.push(item);
            saveQueue(queue);
        }

        markPending(form, item);
        updateStatus();

        if (!item.isWarmup) {
            const card = form.closest(".exercise");
            startRest(card ? Number(card.dataset.rest) : 120);
        }

        flush();
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

    window.addEventListener("online", flush);
    window.addEventListener("pageshow", flush);
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            flush();
            keepAwake();
            tick();
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

    function startRest(seconds) {
        if (!sessionActive || !seconds) {
            return;
        }
        write(REST_KEY, { sessionId, end: Date.now() + seconds * 1000, total: seconds, beeped: false });
        tick();
    }

    function tick() {
        const timer = document.getElementById("rest-timer");
        if (!timer) {
            return;
        }

        const state = read(REST_KEY, null);
        if (!state || state.sessionId !== sessionId) {
            timer.hidden = true;
            return;
        }

        const remaining = Math.max(0, Math.ceil((state.end - Date.now()) / 1000));
        timer.hidden = false;
        document.getElementById("rest-time").textContent =
            `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, "0")}`;
        timer.querySelector(".rest-fill").style.width = `${Math.min(100, (remaining / state.total) * 100)}%`;
        timer.classList.toggle("done", remaining === 0);

        if (remaining === 0 && !state.beeped) {
            state.beeped = true;
            write(REST_KEY, state);
            beep();
        }

        // Clear a finished timer after a few seconds so it doesn't linger.
        if (remaining === 0 && Date.now() - state.end > 4000) {
            write(REST_KEY, null);
            timer.hidden = true;
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
        tick();
    }

    document.addEventListener("click", (event) => {
        if (event.target.closest("#rest-add")) {
            unlockAudio();
            adjustRest(30000);
        } else if (event.target.closest("#rest-skip")) {
            write(REST_KEY, null);
            tick();
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

    restorePending();
    flush();
    keepAwake();
    tick();
    setInterval(tick, 250);
})();
