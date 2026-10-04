// Soft navigation: same-origin links and plain POST forms fetch the next page and swap the top bar, main
// content and overlays inside a View Transition, so the tab bar, scripts and the set queue stay alive.
// Anything unexpected falls back to a normal navigation. Pages are never cached; a prefetch started on
// touch is used once, within a few seconds, and dropped after any write.
(function () {
    "use strict";

    if (!window.fetch || !window.DOMParser || !history.pushState || !window.htmx) {
        return;
    }

    const root = document.documentElement;
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)");
    const PREFETCH_MS = 4000;
    const SLOW_MS = 120;
    let prefetched = null;
    let current = null;
    const loaded = new Set(Array.from(document.scripts, (s) => s.src && new URL(s.src).pathname).filter(Boolean));

    history.scrollRestoration = "manual";

    // Thrown when the response is fine but can't be swapped in (login page, language change):
    // load the final URL normally. Never re-sends a form.
    class FullLoad extends Error {
        constructor(url) {
            super("full load");
            this.url = url;
        }
    }

    function eligibleLink(a, event) {
        if (!a || event.defaultPrevented || event.button > 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return null;
        }
        if (a.target || a.hasAttribute("download") || a.closest("[data-full-nav]") || a.hasAttribute("data-sheet-open") || a.hasAttribute("hx-get")) {
            return null;
        }
        const url = new URL(a.href, location.href);
        if (url.origin !== location.origin || url.pathname.startsWith("/account/") || /\.[a-z0-9]+$/i.test(url.pathname)) {
            return null;
        }
        if (url.pathname === location.pathname && url.search === location.search && url.hash) {
            return null;
        }
        return url;
    }

    function request(url, init) {
        return fetch(url, Object.assign({ credentials: "same-origin", headers: { "X-Gym-Nav": "1" } }, init));
    }

    function takePrefetch(url) {
        const p = prefetched;
        prefetched = null;
        return p && p.url === url.href && performance.now() - p.at < PREFETCH_MS ? p.promise : null;
    }

    // Start loading on touch: on iOS the click arrives 100 to 150 ms after the finger goes down.
    document.addEventListener("pointerdown", (event) => {
        const url = eligibleLink(event.target.closest("a[href]"), event);
        if (url && !(prefetched && prefetched.url === url.href)) {
            const promise = request(url);
            promise.catch(() => {});
            prefetched = { url: url.href, at: performance.now(), promise };
        }
    }, { passive: true });

    // Any write makes a prefetched page stale.
    document.addEventListener("htmx:beforeRequest", (event) => {
        if ((event.detail.requestConfig.verb || "get") !== "get") {
            prefetched = null;
        }
    });

    function navType(fromTab, fromDepth, toTab, toDepth, isBack) {
        if (reduce.matches || fromTab !== toTab || fromDepth === toDepth) {
            return "tab";
        }
        return isBack || toDepth < fromDepth ? "back" : "forward";
    }

    function loadScript(src) {
        const path = new URL(src, location.href).pathname;
        if (loaded.has(path)) {
            return Promise.resolve();
        }
        loaded.add(path);
        return new Promise((resolve, reject) => {
            const s = document.createElement("script");
            s.src = src;
            s.onload = resolve;
            s.onerror = reject;
            document.head.appendChild(s);
        });
    }

    function markActiveTab(tab) {
        document.querySelectorAll(".tabbar .tab").forEach((t) => {
            const on = t.getAttribute("href") === "/" + tab;
            t.classList.toggle("is-active", on);
            if (on) {
                t.setAttribute("aria-current", "page");
            } else {
                t.removeAttribute("aria-current");
            }
        });
    }

    async function swapIn(doc, finalUrl, { push, replace, isBack, scrollY }) {
        const next = doc.documentElement;
        const nextShell = doc.querySelector(".app-scale");
        const shell = document.querySelector(".app-scale");
        // A page outside the app shell, or one in another language (the script strings live in <head>).
        if (!nextShell || !shell || next.lang !== root.lang) {
            throw new FullLoad(finalUrl);
        }

        // Scripts in the incoming content load once, before the content is shown; modules then mount it.
        const scripts = Array.from(doc.querySelectorAll(".app-scale script[src], #overlays script[src]"));
        scripts.forEach((s) => s.remove());
        for (const s of scripts) {
            await loadScript(s.getAttribute("src"));
        }

        const nav = navType(root.dataset.tab, Number(root.dataset.depth || 0), next.dataset.tab, Number(next.dataset.depth || 0), isBack);
        const keepTabbar = !!shell.querySelector(".tabbar") && !!nextShell.querySelector(".tabbar");

        const apply = () => {
            document.dispatchEvent(new CustomEvent("gym:beforeswap"));
            if (keepTabbar) {
                shell.querySelector(".topbar").replaceWith(nextShell.querySelector(".topbar"));
                shell.querySelector("#main").replaceWith(nextShell.querySelector("#main"));
                markActiveTab(next.dataset.tab);
            } else {
                shell.replaceWith(nextShell);
            }
            document.getElementById("overlays").replaceWith(doc.getElementById("overlays"));
            document.body.className = doc.body.className;
            document.body.setAttribute("hx-headers", doc.body.getAttribute("hx-headers") || "");
            ["data-tab", "data-depth", "data-theme"].forEach((a) => root.setAttribute(a, next.getAttribute(a) || ""));
            document.querySelectorAll('meta[name="theme-color"], meta[name="apple-mobile-web-app-status-bar-style"]').forEach((m) => m.remove());
            doc.querySelectorAll('meta[name="theme-color"], meta[name="apple-mobile-web-app-status-bar-style"]').forEach((m) => document.head.appendChild(m));
            document.title = doc.title;
            if (push) {
                history.pushState({ gym: true }, "", finalUrl);
            } else if (replace) {
                history.replaceState({ gym: true }, "", finalUrl);
            }
            window.scrollTo(0, scrollY || 0);
            htmx.process(document.querySelector(".app-scale"));
            htmx.process(document.getElementById("overlays"));
            document.getElementById("main").focus({ preventScroll: true });
            document.dispatchEvent(new CustomEvent("gym:page", { detail: { nav } }));
        };

        if (current) {
            current.skipTransition();
        }
        if (!document.startViewTransition) {
            apply();
            return;
        }
        root.dataset.nav = nav;
        const transition = document.startViewTransition(apply);
        current = transition;
        transition.finished.finally(() => {
            if (current === transition) {
                current = null;
                delete root.dataset.nav;
            }
        });
        await transition.updateCallbackDone;
    }

    async function go(url, { push = true, init = null, isBack = false, scrollY = 0, fallback }) {
        // Remember where we were, for Back.
        history.replaceState(Object.assign({}, history.state, { gym: true, scrollY: window.scrollY }), "");
        const slow = setTimeout(() => root.classList.add("is-navigating"), SLOW_MS);
        try {
            const response = await ((!init && takePrefetch(url)) || request(url, init || undefined));
            const finalUrl = new URL(response.url);
            const html = (response.headers.get("Content-Type") || "").includes("text/html");
            if (finalUrl.pathname.startsWith("/account/")) {
                throw new FullLoad(finalUrl.href);
            }
            if (!html || response.status >= 500) {
                if (init) {
                    // The form reached the server and failed there; sending it again could do it twice.
                    window.gymToast(t("Something went wrong ({0}). Try again.", response.status));
                    return;
                }
                throw new Error("not a page");
            }
            const doc = new DOMParser().parseFromString(await response.text(), "text/html");
            const samePage = finalUrl.href === location.href;
            await swapIn(doc, finalUrl.href, { push: push && !samePage, replace: !push || samePage, isBack, scrollY });
        } catch (error) {
            if (error instanceof FullLoad) {
                location.href = error.url;
            } else {
                fallback();
            }
        } finally {
            clearTimeout(slow);
            root.classList.remove("is-navigating");
        }
    }

    window.gymNav = (href, options) => {
        const url = new URL(href, location.href);
        return go(url, Object.assign({ fallback: () => { location.href = url.href; } }, options));
    };

    document.addEventListener("click", (event) => {
        const a = event.target.closest("a[href]");
        const url = eligibleLink(a, event);
        if (!url) {
            return;
        }
        event.preventDefault();
        if (a.closest(".tabbar")) {
            markActiveTab(url.pathname.split("/")[1] || "today");
        }
        go(url, { fallback: () => { location.href = url.href; } });
    });

    // Plain POST forms (no htmx, not account pages): submit with fetch, follow the redirect, swap the result.
    document.addEventListener("submit", (event) => {
        const form = event.target;
        if (event.defaultPrevented || !(form instanceof HTMLFormElement) || form.method.toLowerCase() !== "post") {
            return;
        }
        if (form.closest("[data-full-nav]") || form.hasAttribute("hx-post") || form.target || form.enctype === "multipart/form-data") {
            return;
        }
        const action = new URL(form.action, location.href);
        if (action.origin !== location.origin || action.pathname.startsWith("/account/")) {
            return;
        }
        event.preventDefault();
        prefetched = null;
        const submitter = event.submitter;
        go(action, {
            init: { method: "POST", body: new URLSearchParams(new FormData(form, submitter)) },
            // No answer: the change may or may not have been saved, so say so instead of sending it again.
            fallback: () => window.gymToast(t("No connection. That change was not saved; try again when you have signal.")),
        });
    });

    window.addEventListener("popstate", (event) => {
        const state = event.state || {};
        go(new URL(location.href), { push: false, isBack: true, scrollY: state.scrollY || 0, fallback: () => location.reload() });
    });
})();
