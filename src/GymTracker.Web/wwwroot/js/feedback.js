// Feedback (SPEC 11): parse ratings, the Send feedback form, and browser error reports.
//
// Ratings and reports are written to localStorage before they are sent, like sets (session.js), so a report made
// with no signal is sent later. Each has a ClientId; the server treats a repeat as the same report.
// Loaded without defer, so errors thrown while the other scripts start are caught too.
(function () {
    "use strict";

    const queue = window.gymQueue("gym.pendingFeedback");
    const ERRORS_KEY = "gym.recentErrors.v2." + encodeURIComponent(document.documentElement.dataset.userId || "");
    const RETRY_MS = 15000;
    const MAX_SIDE = 1600;
    const JPEG_QUALITY = 0.8;
    const MAX_BYTES = 850 * 1024; // under the server's 900 KB, with room for the rest of the form

    // ---- storage ----

    function read(store, key, fallback) {
        try {
            const raw = store.getItem(key);
            return raw ? JSON.parse(raw) : fallback;
        } catch {
            return fallback;
        }
    }

    function write(store, key, value) {
        try {
            if (value === null) {
                store.removeItem(key);
            } else {
                store.setItem(key, JSON.stringify(value));
            }
        } catch {
            // Private browsing or a full disk: the in-flight request may still succeed.
        }
    }

    const loadQueue = queue.load;
    const saveQueue = queue.save;

    // Shown once while sends keep failing the same way, not on every retry.
    let told = null;
    function tellOnce(message) {
        if (told !== message && window.gymToast) {
            told = message;
            window.gymToast(message);
        }
    }

    const uuid = () => (crypto.randomUUID ? crypto.randomUUID()
        : "10000000-1000-4000-8000-100000000000".replace(/[018]/g, (c) => (c ^ (crypto.getRandomValues(new Uint8Array(1))[0] & (15 >> (c / 4)))).toString(16)));

    function signedIn() {
        return queue.active();
    }

    function device() {
        return [
            ["viewport", `${window.innerWidth}x${window.innerHeight}`],
            ["standalone", String(window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true)],
        ];
    }

    // ---- queue ----

    function enqueue(item) {
        const pending = loadQueue();
        if (!pending.some((q) => q.key === item.key)) {
            pending.push(item);
            if (!saveQueue(pending) && window.gymToast) {
                window.gymToast(t("This browser can't save pending changes. Keep this page open until they are sent."));
            }
        }
        renderPending();
    }

    function remove(key) {
        saveQueue(loadQueue().filter((q) => q.key !== key));
        renderPending();
    }

    // "next" carries on with the queue, "stop" waits for the next retry.
    async function send(item) {
        let response;
        try {
            response = await fetch(item.url, {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/x-www-form-urlencoded",
                    "RequestVerificationToken": window.gymToken ? window.gymToken() : "",
                    "HX-Request": "true",
                    "X-Gym-Queue": "1",
                    ...queue.headers(),
                },
                body: new URLSearchParams(item.fields),
            });
        } catch {
            return "stop";
        }

        if (response.ok) {
            told = null;
            remove(item.key);
            return "next";
        }
        if (response.status === 409) {
            if (queue.durable) {
                queue.pause();
                window.gymToast && window.gymToast(t("Your sign-in changed. Reload this page before sending pending changes."));
            } else {
                tellOnce(t("Your sign-in changed. Sign back into this account in another tab, then return here to send pending changes."));
            }
            return "stop";
        }
        if (response.status === 401 || response.status === 429 || response.status >= 500) {
            return "stop"; // signed out (sends after logging in), too many, or the server is restarting
        }
        // Will never succeed (invalid, or the parse is gone): drop it and say why.
        remove(item.key);
        const text = response.status === 400 ? await response.text() : "";
        if (window.gymToast) {
            window.gymToast(text || t("Something went wrong ({0}). Try again.", response.status));
        }
        return "next";
    }

    let sending = null;
    // Sends the queue in order, one request at a time. Resolves when it is empty or a send has to wait.
    function drain() {
        if (sending || !signedIn()) {
            return sending || Promise.resolve();
        }
        sending = (async () => {
            // Yield first: with an empty queue the body would otherwise finish, and clear `sending`, before this
            // assignment, leaving a settled promise that blocks every later drain.
            await null;
            try {
                for (;;) {
                    if (!signedIn()) {
                        return;
                    }
                    const next = loadQueue()[0];
                    if (!next || (await send(next)) === "stop") {
                        return;
                    }
                }
            } finally {
                sending = null;
            }
        })();
        return sending;
    }

    window.addEventListener("online", drain);
    window.addEventListener("pageshow", drain);
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            drain();
        }
    });
    setInterval(() => {
        if (loadQueue().length) {
            drain();
        }
    }, RETRY_MS);
    document.addEventListener("DOMContentLoaded", drain);

    // ---- reports waiting on this phone (Your feedback page) ----

    function renderPending() {
        const section = document.querySelector('[data-module="feedback-pending"]');
        if (!section) {
            return;
        }
        const reports = loadQueue().filter((q) => q.kind === "report");
        const list = section.querySelector("[data-pending-list]");
        list.replaceChildren(...reports.map((r) => {
            const row = document.createElement("div");
            row.className = "row";
            const text = document.createElement("span");
            text.className = "row-main row-ellipsis";
            text.textContent = r.preview || t("Report");
            row.appendChild(text);
            return row;
        }));
        section.hidden = reports.length === 0;
    }

    window.gymModule("feedback-pending", () => {
        renderPending();
        drain().then(renderPending);
    });

    // ---- reports and ratings saved before queues had owners (Your feedback page) ----
    // Nothing on the server can tell whose a report is, so the person signed in decides: send it from this account,
    // or discard it. Discard asks first (data-confirm on its form, site.js).

    window.gymModule("feedback-legacy", (section) => {
        const list = section.querySelector("[data-legacy-list]");
        const sendForm = section.querySelector("[data-legacy-send]");
        const discardForm = section.querySelector("[data-legacy-discard]");

        function render() {
            const items = queue.legacyItems();
            list.replaceChildren(...items.map((r) => {
                const row = document.createElement("div");
                row.className = "row";
                const text = document.createElement("span");
                text.className = "row-main row-ellipsis";
                text.textContent = r.preview || (r.kind === "report" ? t("Report") : t("Food rating"));
                row.appendChild(text);
                return row;
            }));
            section.hidden = items.length === 0;
        }

        const onSend = (event) => {
            event.preventDefault();
            queue.adoptLegacy((item) => item.key);
            render();
            renderPending();
            drain().then(renderPending);
        };
        const onDiscard = (event) => {
            event.preventDefault();
            queue.discardLegacy();
            render();
        };
        sendForm.addEventListener("submit", onSend);
        discardForm.addEventListener("submit", onDiscard);
        render();
        return () => {
            sendForm.removeEventListener("submit", onSend);
            discardForm.removeEventListener("submit", onDiscard);
        };
    });

    // ---- ratings on the Check foods screen ----

    window.gymModule("parse-rating", (form) => {
        const run = form.dataset.run;
        const sheet = document.getElementById("parse-sheet");
        form.querySelectorAll("[data-rating], [data-rate][hidden]").forEach((el) => { el.hidden = false; });

        function rate(button) {
            const rating = button.dataset.rate;
            const item = button.dataset.item;
            const clientId = uuid();
            const fields = [["clientId", clientId], ["rating", rating], ...device()];
            if (item !== undefined) {
                fields.push(["item", item]);
            }
            enqueue({ key: clientId, kind: "rating", url: `/feedback/parse/${run}`, fields });
            drain();

            // One rating per item (or for the whole list): the other button lets go.
            const scope = item === undefined ? form.querySelector("[data-rating]") : button.closest(".result-head");
            scope.querySelectorAll("[data-rate]").forEach((b) => b.setAttribute("aria-pressed", String(b === button)));
            if (item === undefined) {
                const question = form.querySelector("[data-rating-question]");
                question.textContent = rating === "Up" ? t("Thanks!") : t("Thanks. What was wrong?");
            }
            if (rating === "Down" && sheet) {
                openSheet(clientId, item);
            }
        }

        function openSheet(clientId, item) {
            sheet.dataset.clientId = clientId;
            sheet.querySelectorAll('input[name="reasons"]').forEach((c) => { c.checked = false; });
            sheet.querySelector("textarea").value = "";
            const label = sheet.querySelector("[data-sheet-item]");
            const description = item === undefined ? null : form.querySelector(`input[name="items[${item}].Description"]`);
            label.hidden = !description;
            label.textContent = description ? description.value : "";
            window.gymSheet.show(sheet);
        }

        const onClick = (event) => {
            const button = event.target.closest("[data-rate]");
            if (button && form.contains(button)) {
                event.preventDefault();
                rate(button);
            }
        };
        form.addEventListener("click", onClick);

        const onSend = (event) => {
            if (!event.target.closest("[data-send-details]")) {
                return;
            }
            const clientId = sheet.dataset.clientId;
            const fields = [["clientId", clientId]];
            sheet.querySelectorAll('input[name="reasons"]:checked').forEach((c) => fields.push(["reasons", c.value]));
            const message = sheet.querySelector("textarea").value.trim();
            if (message) {
                fields.push(["message", message]);
            }
            enqueue({ key: clientId + ":details", kind: "details", url: "/feedback/parse/details", fields });
            drain();
            window.gymSheet.hide();
            if (window.gymToast) {
                window.gymToast(t("Thanks. This helps it read food better."));
            }
        };
        if (sheet) {
            sheet.addEventListener("click", onSend);
        }

        return () => {
            form.removeEventListener("click", onClick);
            if (sheet) {
                sheet.removeEventListener("click", onSend);
            }
        };
    });

    // ---- the Send feedback form ----

    // Screenshots stay far under the 1 MB request limit (image.js does the drawing).
    function shrink(file) {
        return window.gymImage.shrink(file, {
            maxSide: MAX_SIDE,
            quality: JPEG_QUALITY,
            maxBytes: MAX_BYTES,
            steps: [[MAX_SIDE, 0.6], [1200, 0.6], [900, 0.5]],
        });
    }

    window.gymModule("feedback-form", (form) => {
        const input = form.querySelector('input[type="file"]');
        const preview = form.querySelector("[data-shot-preview]");
        const pick = form.querySelector("[data-shot-pick]");
        const offlineNote = form.querySelector("[data-shot-offline]");
        const queued = form.querySelector("[data-queued]");
        let shot = null;
        let previewUrl = null;
        let pendingClientId = null;
        const disabledBeforeSend = new Map();
        const queuedText = queued.querySelector("[data-queued-text]");
        const savedNotice = queuedText.textContent;

        function lockReport(clientId) {
            pendingClientId = clientId;
            form.querySelectorAll('input:not([type="hidden"]), select, textarea, button[type="button"]').forEach((control) => {
                disabledBeforeSend.set(control, control.disabled);
                control.disabled = true;
            });
        }

        function unlockReport() {
            disabledBeforeSend.forEach((disabled, control) => { control.disabled = disabled; });
            disabledBeforeSend.clear();
            pendingClientId = null;
            queuedText.textContent = savedNotice;
            updateOnline();
        }

        function setShot(next) {
            shot = next;
            if (previewUrl) {
                URL.revokeObjectURL(previewUrl);
                previewUrl = null;
            }
            preview.hidden = !shot;
            if (shot) {
                previewUrl = URL.createObjectURL(shot.blob);
                preview.querySelector("img").src = previewUrl;
                preview.querySelector("[data-shot-size]").textContent = `${Math.round(shot.blob.size / 1024)} KB`;
            }
            input.value = "";
            updateOnline();
        }

        function updateOnline() {
            const online = navigator.onLine;
            input.disabled = !!pendingClientId || !online;
            pick.hidden = !!shot;
            pick.style.opacity = online ? "" : "0.4";
            offlineNote.hidden = online;
        }

        const onChange = async () => {
            const file = input.files && input.files[0];
            if (!file) {
                return;
            }
            try {
                setShot(await shrink(file));
            } catch {
                setShot(null);
                window.gymToast && window.gymToast(t("Couldn't read that picture. Try a screenshot."));
            }
        };
        input.addEventListener("change", onChange);

        const onRemove = (event) => {
            if (event.target.closest("[data-shot-remove]")) {
                setShot(null);
            }
        };
        form.addEventListener("click", onRemove);

        function fields() {
            const data = new FormData(form);
            data.delete("screenshot");
            device().forEach(([k, v]) => data.set(k, v));
            data.set("recentErrors", JSON.stringify(read(sessionStorage, ERRORS_KEY, [])));
            data.delete("__RequestVerificationToken");
            return [...data.entries()].map(([k, v]) => [k, String(v)]);
        }

        async function sendWithShot(button) {
            const data = new FormData();
            fields().forEach(([k, v]) => data.append(k, v));
            data.set("screenshotWidth", String(shot.width));
            data.set("screenshotHeight", String(shot.height));
            data.append("screenshot", shot.blob, "screenshot.jpg");
            button.disabled = true;
            try {
                const response = await fetch(form.action, {
                    method: "POST",
                    credentials: "same-origin",
                    headers: { "RequestVerificationToken": window.gymToken(), "HX-Request": "true", ...queue.headers() },
                    body: data,
                });
                if (response.ok) {
                    done();
                    return;
                }
                window.gymToast(response.status === 400 ? await response.text() : t("Something went wrong ({0}). Try again.", response.status));
            } catch {
                window.gymToast(t("No connection. Remove the screenshot to keep the message on this phone, or try again with signal."));
            } finally {
                button.disabled = false;
            }
        }

        function done() {
            const target = "/feedback?sent=1";
            window.gymNav ? window.gymNav(target) : (location.href = target);
        }

        const onSubmit = async (event) => {
            event.preventDefault();
            const button = event.submitter || form.querySelector("button[type=submit]");
            const clientField = form.querySelector('input[name="clientId"]');
            if (!pendingClientId) {
                if (!form.reportValidity()) {
                    return;
                }
                if (shot) {
                    await sendWithShot(button);
                    return;
                }
                const clientId = clientField.value;
                const message = (form.querySelector("textarea[name=message]").value || "").trim();
                enqueue({ key: clientId, kind: "report", url: form.action, fields: fields(), preview: message.slice(0, 80) });
                // A request may have reached the server even if its response is lost. Keep its id and
                // contents immutable until it is acknowledged, including while the first request is in flight.
                lockReport(clientId);
            }
            const clientId = pendingClientId;
            button.disabled = true;
            await drain();
            button.disabled = false;
            if (!loadQueue().some((q) => q.key === clientId)) {
                done();
                return;
            }
            if (!queue.durable) {
                queuedText.textContent = t("This report is waiting to send. Its contents are locked. Keep this page open and press Send to retry.");
                queued.hidden = false;
                window.gymToast(t("This browser can't save pending changes. Keep this page open until they are sent."));
                return;
            }
            // Still waiting for signal: keep it on the phone and start a fresh report.
            unlockReport();
            clientField.value = uuid();
            form.querySelector("textarea[name=message]").value = "";
            form.querySelectorAll('input[name="reasons"]').forEach((c) => { c.checked = false; });
            queued.hidden = false;
        };
        form.addEventListener("submit", onSubmit);
        window.addEventListener("online", updateOnline);
        window.addEventListener("offline", updateOnline);
        updateOnline();

        return () => {
            input.removeEventListener("change", onChange);
            form.removeEventListener("click", onRemove);
            form.removeEventListener("submit", onSubmit);
            window.removeEventListener("online", updateOnline);
            window.removeEventListener("offline", updateOnline);
            setShot(null);
        };
    });

    // ---- browser errors ----

    const reported = new Set();
    let pendingErrors = [];

    function capture(message, stack, file) {
        if (!message || message === "Script error." || /ResizeObserver loop/.test(message)
            || /Transition was (aborted|skipped)|Skipping view transition|view transition aborted/i.test(message)) {
            return; // cross-origin noise, a harmless browser warning, or a page transition skipped (phone locked)
        }
        if (file && !file.startsWith(location.origin)) {
            return; // an extension, not the app
        }
        const key = message + "\n" + (stack || "").split("\n").slice(0, 3).join("\n");
        if (reported.has(key)) {
            return;
        }
        reported.add(key);
        const error = { message: String(message).slice(0, 500), stack: String(stack || "").slice(0, 3000), page: location.pathname + location.search };
        const recent = read(sessionStorage, ERRORS_KEY, []);
        recent.push({ message: error.message, page: error.page, at: new Date().toISOString() });
        write(sessionStorage, ERRORS_KEY, recent.slice(-5));
        pendingErrors.push(error);
        flushErrors();
    }

    // Sent as they happen, without a queue: an error report lost to bad signal is not worth retrying.
    function flushErrors() {
        if (!signedIn() || !window.gymToken || document.readyState === "loading") {
            return;
        }
        const errors = pendingErrors;
        pendingErrors = [];
        errors.forEach((error) => {
            fetch("/feedback/client-error", {
                method: "POST",
                credentials: "same-origin",
                keepalive: true,
                headers: { "Content-Type": "application/x-www-form-urlencoded", "RequestVerificationToken": window.gymToken(), "HX-Request": "true" },
                body: new URLSearchParams({ ...error, version: document.documentElement.dataset.version || "" }),
            }).catch(() => {});
        });
    }

    window.addEventListener("error", (event) => {
        capture(event.message, event.error && event.error.stack, event.filename);
    });
    window.addEventListener("unhandledrejection", (event) => {
        const reason = event.reason;
        capture(reason && reason.message ? reason.message : String(reason), reason && reason.stack, null);
    });
    window.addEventListener("load", flushErrors);
})();
