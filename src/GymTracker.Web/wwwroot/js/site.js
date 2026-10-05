// Shows htmx failures as a toast instead of failing silently, registers the service worker,
// and keeps the live per-portion totals on the food forms up to date.
(function () {
    "use strict";

    // Optional content loaded in the background (the weekly summary) just disappears when it can't load.
    const quiet = (event) => {
        const el = event.target.closest && event.target.closest("[data-quiet-on-error]");
        if (el) {
            el.remove();
            return true;
        }
        return false;
    };

    document.body.addEventListener("htmx:responseError", (event) => {
        if (quiet(event)) {
            return;
        }
        const xhr = event.detail.xhr;
        if (xhr.status === 401 || xhr.responseURL.includes("/account/login")) {
            window.location.href = "/account/login?returnUrl=" + encodeURIComponent(window.location.pathname);
            return;
        }
        window.gymToast(xhr.status === 400 && xhr.responseText ? xhr.responseText : t("Something went wrong ({0}). Try again.", xhr.status));
    });

    document.body.addEventListener("htmx:sendError", (event) => {
        if (quiet(event)) {
            return;
        }
        window.gymToast(t("No connection. That change was not saved; try again when you have signal."));
    });

    // A poll that fails (the admin area's eval progress, when the Cloudflare Access session has run out) reloads the
    // page, so the browser follows Access's login instead of polling into the void.
    for (const name of ["htmx:sendError", "htmx:responseError"]) {
        document.body.addEventListener(name, (event) => {
            if (event.target.closest && event.target.closest("[data-reload-on-error]")) {
                location.reload();
            }
        });
    }

    // Settings that apply as soon as they change. A theme change previews at once with a crossfade, then saves.
    document.addEventListener("change", (event) => {
        const form = event.target.closest("form[data-autosubmit]");
        if (!form) {
            return;
        }
        const theme = event.target.dataset.themePreview;
        const submit = () => form.requestSubmit();
        if (!theme) {
            submit();
            return;
        }
        const apply = () => { document.documentElement.dataset.theme = theme; };
        if (document.startViewTransition && !window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
            document.startViewTransition(apply).finished.then(submit, submit);
        } else {
            apply();
            submit();
        }
    });

    // Destructive and final forms ask first, in the app's own sheet (SPEC 7). Capture phase, so it runs before the busy
    // state and soft navigation; once confirmed, the form is sent again with the same button and goes through.
    // data-confirm-action labels the button (else the form's own button text); data-confirm-style="primary" for an
    // action that isn't destructive (Finish). Finish says how many planned sets aren't logged.
    function confirmText(form) {
        if (form.dataset.confirmUnloggedMany) {
            const blank = document.querySelectorAll(".rows.working form[data-log-set]:not(.pending)").length;
            if (blank === 1) {
                return form.dataset.confirmUnloggedOne;
            }
            if (blank > 1) {
                return form.dataset.confirmUnloggedMany.replace("{0}", String(blank));
            }
        }
        return form.dataset.confirm;
    }

    document.addEventListener("submit", (event) => {
        const form = event.target.closest("form[data-confirm]");
        if (!form) {
            return;
        }
        if (form.dataset.confirmed === "true") {
            delete form.dataset.confirmed;
            return;
        }
        event.preventDefault();
        event.stopImmediatePropagation();
        const sheet = document.getElementById("confirm-sheet");
        const submitter = event.submitter;
        sheet.querySelector("[data-confirm-text]").textContent = confirmText(form);
        const yes = sheet.querySelector("[data-confirm-yes]");
        yes.textContent = form.dataset.confirmAction || (submitter && submitter.textContent.trim()) || t("OK");
        yes.className = "btn btn-block " + (form.dataset.confirmStyle === "primary" ? "btn-primary" : "btn-destructive");
        yes.onclick = () => {
            yes.onclick = null;
            window.gymSheet.hide();
            form.dataset.confirmed = "true";
            form.requestSubmit(submitter && submitter.form === form ? submitter : undefined);
        };
        window.gymSheet.show(sheet);
    }, true);

    // ---- forms that save on change (SPEC 7: logged sets, template exercises, Settings) ----
    // A form with data-autosave and hx-post sends itself on change (its hx-trigger). It is "dirty" from the first
    // keystroke until its request starts, "saving" while it runs and "unsaved" when that failed; the server marks the
    // form it sends back as saved. What is still dirty is sent before the page is left, and failures retry on reconnect.

    const AUTOSAVE = "form[data-autosave]";

    document.addEventListener("input", (event) => {
        const form = event.target.closest(AUTOSAVE);
        if (form) {
            form.classList.add("is-dirty");
            form.classList.remove("is-saved", "is-unsaved");
        }
    });
    document.body.addEventListener("htmx:beforeRequest", (event) => {
        const form = event.detail.elt;
        if (form.matches && form.matches(AUTOSAVE)) {
            form.classList.remove("is-dirty", "is-unsaved");
            form.classList.add("is-saving");
        }
    });
    for (const name of ["htmx:responseError", "htmx:sendError"]) {
        document.body.addEventListener(name, (event) => {
            const form = event.detail.elt;
            if (form.matches && form.matches(AUTOSAVE)) {
                form.classList.remove("is-saving");
                form.classList.add("is-unsaved");
            }
        });
    }

    function sendNow(form) {
        form.classList.remove("is-dirty");
        form.classList.add("is-saving");
        return fetch(form.getAttribute("hx-post"), {
            method: "POST",
            credentials: "same-origin",
            // Survives the page going away (the app closed, or a full load).
            keepalive: true,
            headers: {
                "Content-Type": "application/x-www-form-urlencoded",
                "RequestVerificationToken": window.gymToken(),
                "HX-Request": "true",
            },
            body: new URLSearchParams(new FormData(form)),
        });
    }

    // nav.js awaits this before it leaves the page; at most 3 seconds, so a dead connection never traps the user.
    window.gymFlushEdits = () => {
        const dirty = Array.from(document.querySelectorAll(AUTOSAVE + ".is-dirty"));
        if (dirty.length === 0) {
            return Promise.resolve();
        }
        const sent = Promise.allSettled(dirty.map((form) => sendNow(form)));
        return Promise.race([sent, new Promise((resolve) => setTimeout(resolve, 3000))]);
    };
    window.addEventListener("pagehide", () => {
        document.querySelectorAll(AUTOSAVE + ".is-dirty").forEach((form) => sendNow(form).catch(() => {}));
    });

    function retryUnsaved() {
        if (window.htmx) {
            document.querySelectorAll(AUTOSAVE + ".is-unsaved").forEach((form) => window.htmx.trigger(form, "submit"));
        }
    }
    window.addEventListener("online", retryUnsaved);
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            retryUnsaved();
        }
    });

    // A template renamed in place: its titles follow (the large title, the compact one, the tab's).
    document.body.addEventListener("gym:renamed", (event) => {
        const name = event.detail.name;
        document.querySelectorAll(".large-title, .topbar-title").forEach((el) => { el.textContent = name; });
        document.title = name + " · Gym";
    });

    // Forms that take a while (the food text parser): show the busy state and stop a second send.
    document.addEventListener("submit", (event) => {
        const form = event.target.closest("form[data-busy]");
        if (form && !event.defaultPrevented) {
            form.classList.add("is-busy");
            form.querySelectorAll("button[type=submit]").forEach((b) => { b.disabled = true; });
        }
    });
    window.addEventListener("pageshow", () => {
        document.querySelectorAll("form.is-busy").forEach((form) => {
            form.classList.remove("is-busy");
            form.querySelectorAll("button[type=submit]").forEach((b) => { b.disabled = false; });
        });
    });

    // The anti-forgery token for fetch requests: the layout puts it in <body hx-headers>, and nav.js keeps it
    // current across page swaps. Read it at send time, never store it.
    window.gymToken = () => {
        try {
            return JSON.parse(document.body.getAttribute("hx-headers") || "{}").RequestVerificationToken || "";
        } catch {
            return "";
        }
    };

    if ("serviceWorker" in navigator) {
        navigator.serviceWorker.register("/sw.js").catch(() => {});
    }

    // Live per-row totals on the food confirmation list and portion forms.
    document.addEventListener("input", (event) => {
        const row = event.target.closest("[data-per100]");
        if (!row) {
            return;
        }
        const grams = parseFloat((row.querySelector('input[name$="Grams"], input[name="grams"]') || {}).value) || 0;
        const per = row.dataset.per100.split(",").map(Number);
        const out = row.querySelector("[data-totals]");
        if (out) {
            const f = grams / 100;
            out.textContent = t("{0} kcal · {1} g protein", Math.round(per[0] * f), (per[1] * f).toFixed(1));
        }
    });
})();
