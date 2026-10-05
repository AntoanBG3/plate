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

    // Destructive forms ask first. Capture phase, so it runs before the busy state and soft navigation.
    document.addEventListener("submit", (event) => {
        const form = event.target.closest("form[data-confirm]");
        if (form && !window.confirm(form.dataset.confirm)) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    }, true);

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
