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
    let navigation = 0;
    const loaded = new Set(Array.from(document.scripts, (s) => s.src && new URL(s.src).pathname).filter(Boolean));

    history.scrollRestoration = "manual";

    // Thrown when the response can't be swapped into this page: a different shell (signed in or out, another
    // language) or no app shell at all. Loads the final URL normally; never re-sends a form.
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
        if (a.target || a.hasAttribute("download")) {
            return null;
        }
        const url = new URL(a.href, location.href);
        if (url.origin !== location.origin || /\.[a-z0-9]+$/i.test(url.pathname)) {
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

    function dropPrefetch() {
        if (prefetched) {
            prefetched.abort.abort();
            prefetched = null;
        }
    }

    // Start loading on touch: on iOS the click arrives 100 to 150 ms after the finger goes down.
    document.addEventListener("pointerdown", (event) => {
        const url = eligibleLink(event.target.closest("a[href]"), event);
        // An unsaved edit on this page is sent first (go), so a page fetched now could miss it.
        if (url && !(prefetched && prefetched.url === url.href) && !document.querySelector(".is-dirty")) {
            dropPrefetch();
            const abort = new AbortController();
            const promise = request(url, { signal: abort.signal });
            promise.catch(() => {});
            prefetched = { url: url.href, at: performance.now(), promise, abort };
        }
    }, { passive: true });

    // A tap during a page transition is for the new page, which is already in place under the animation (its
    // ::view-transition overlay lets taps through, app.css): finish the animation at once so what is seen matches.
    document.addEventListener("pointerdown", () => {
        if (current) {
            current.skipTransition();
        }
    }, { capture: true, passive: true });

    // The touch became a scroll (iOS cancels the pointer), so the page won't be opened.
    document.addEventListener("pointercancel", dropPrefetch, { passive: true });

    // Any write makes a prefetched page stale.
    document.addEventListener("htmx:beforeRequest", (event) => {
        if ((event.detail.requestConfig.verb || "get") !== "get") {
            dropPrefetch();
        }
    });

    // Position in the tab bar, or -1 on pages without one (signed out).
    function tabIndex(tab) {
        return Array.from(document.querySelectorAll(".tabbar .tab")).findIndex((t) => t.getAttribute("href") === "/" + tab);
    }

    // "forward" and "back" push and pop within a tab. "next" and "prev" slide sideways: to a tab further right or
    // left in the tab bar, or to a sibling page (`side`, from a segmented control). "swap" is the same page with
    // other contents (`swap`: a chart range, another day, a row deleted), which crossfades in place. "tab" fades the
    // new page in when the direction is unknown.
    function navType(fromTab, fromDepth, toTab, toDepth, isBack, side, swap) {
        if (reduce.matches) {
            return "tab";
        }
        if (fromTab !== toTab) {
            const from = tabIndex(fromTab);
            const to = tabIndex(toTab);
            return from < 0 || to < 0 ? "tab" : to > from ? "next" : "prev";
        }
        if (fromDepth === toDepth) {
            return side || (swap ? "swap" : "tab");
        }
        return isBack || toDepth < fromDepth ? "back" : "forward";
    }

    // In a segmented control that switches between sibling pages (data-slide), a segment to the right of the
    // active one is the next page over. Null for any other link, including filters like a chart range.
    function segmentSide(a) {
        const control = a.closest(".segmented[data-slide]");
        if (!control) {
            return null;
        }
        const segments = Array.from(control.querySelectorAll("a.segment"));
        const from = segments.findIndex((s) => s.classList.contains("is-active"));
        const to = segments.indexOf(a);
        return from < 0 || to < 0 || from === to ? null : to > from ? "next" : "prev";
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
            s.onerror = (error) => {
                loaded.delete(path);
                s.remove();
                reject(error);
            };
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

    // Makes `to` carry exactly the attributes of `from`, except the ones listed in `keep`.
    function copyAttributes(from, to, keep) {
        Array.from(to.attributes).forEach((a) => {
            if (!keep.includes(a.name) && !from.hasAttribute(a.name)) {
                to.removeAttribute(a.name);
            }
        });
        Array.from(from.attributes).forEach((a) => to.setAttribute(a.name, a.value));
    }

    // The server names the element a change was made to (Notices.Focus); bring it into view if it isn't.
    function scrollToFocus() {
        const marker = document.querySelector("#main [data-scroll-to]");
        const target = marker && document.getElementById(marker.dataset.scrollTo);
        if (!target) {
            return;
        }
        const r = target.getBoundingClientRect();
        if (r.top < 60 || r.top > window.innerHeight - 160) {
            target.scrollIntoView({ block: "start" });
        }
    }
    scrollToFocus();

    async function swapIn(doc, finalUrl, { push, isBack, scrollY, side, instant, same, swap, posted, isCurrent }) {
        if (!isCurrent()) {
            return;
        }
        const next = doc.documentElement;
        const nextShell = doc.querySelector(".app-scale");
        const shell = document.querySelector(".app-scale");
        // data-shell is the language and signed-in state: the script strings and the session live outside the swap.
        // data-version changes on deploy: the stylesheets and scripts in <head> are the old ones until a full load.
        if (!nextShell || !shell || next.dataset.shell !== root.dataset.shell || next.dataset.userId !== root.dataset.userId || next.dataset.version !== root.dataset.version) {
            throw new FullLoad(finalUrl);
        }

        // Scripts in the incoming content load once, before the content is shown; modules then mount it.
        const scripts = Array.from(doc.querySelectorAll(".app-scale script[src], #overlays script[src]"));
        scripts.forEach((s) => s.remove());
        for (const s of scripts) {
            if (!isCurrent()) {
                return;
            }
            await loadScript(s.getAttribute("src"));
        }
        if (!isCurrent()) {
            return;
        }

        // The same page shown again after a change on it ("same") isn't arriving anywhere: it crossfades in place.
        const nav = same ? "same" : navType(root.dataset.tab, Number(root.dataset.depth || 0), next.dataset.tab, Number(next.dataset.depth || 0), isBack, side, swap);
        const keepTabbar = !!shell.querySelector(".tabbar") && !!nextShell.querySelector(".tabbar");

        const apply = () => {
            if (!isCurrent()) {
                return;
            }
            document.dispatchEvent(new CustomEvent("gym:beforeswap"));
            if (keepTabbar) {
                shell.querySelector(".topbar").replaceWith(nextShell.querySelector(".topbar"));
                shell.querySelector("#main").replaceWith(nextShell.querySelector("#main"));
                markActiveTab(next.dataset.tab);
            } else {
                shell.replaceWith(nextShell);
            }
            document.getElementById("overlays").replaceWith(doc.getElementById("overlays"));
            copyAttributes(next, root, ["data-nav", "class"]);
            copyAttributes(doc.body, document.body, []);
            document.querySelectorAll("head [data-page-head]").forEach((el) => el.remove());
            doc.querySelectorAll("head [data-page-head]").forEach((el) => document.head.appendChild(el));
            document.title = doc.title;
            if (push) {
                history.pushState({ gym: true }, "", finalUrl);
            } else {
                history.replaceState({ gym: true }, "", finalUrl);
            }
            window.scrollTo(0, scrollY || 0);
            scrollToFocus();
            htmx.process(document.querySelector(".app-scale"));
            htmx.process(document.getElementById("overlays"));
            // A form sent back with an error marks the field to fix with autofocus, which a swap doesn't apply by itself.
            // Pages merely opened keep focus on main, so a link never pops up the keyboard.
            const field = posted && document.querySelector("#main [autofocus]");
            (field || document.getElementById("main")).focus({ preventScroll: true });
            document.dispatchEvent(new CustomEvent("gym:page", { detail: { nav } }));
        };

        if (current) {
            current.skipTransition();
        }
        if (!document.startViewTransition || instant) {
            apply();
            return;
        }
        root.dataset.nav = nav;
        const transition = document.startViewTransition(apply);
        // A transition is skipped when the page is hidden or the viewport changes mid-way; the page is still swapped.
        transition.ready.catch(() => {});
        current = transition;
        transition.finished.catch(() => {}).finally(() => {
            if (current === transition) {
                current = null;
                delete root.dataset.nav;
            }
        });
        await transition.updateCallbackDone;
    }

    async function go(url, { push = true, init = null, isBack = false, scrollY = 0, side = null, instant = false, keepScroll = false, fallback = () => { location.href = url.href; } } = {}) {
        const generation = ++navigation;
        const isCurrent = () => generation === navigation;
        // Remember where we were, for Back.
        history.replaceState(Object.assign({}, history.state, { gym: true, scrollY: window.scrollY }), "");
        const slow = setTimeout(() => {
            if (isCurrent()) {
                root.classList.add("is-navigating");
            }
        }, SLOW_MS);
        try {
            // A set edit still being typed is saved first, so the next page (or this one again) shows it.
            if (window.gymFlushEdits) {
                await window.gymFlushEdits();
            }
            // A submitted write must still run, even if the user chose another page while edits were flushing.
            if (!isCurrent() && !init) {
                return;
            }
            const response = await ((!init && takePrefetch(url)) || request(url, init || undefined));
            if (!isCurrent()) {
                return;
            }
            const finalUrl = new URL(response.url);
            const html = (response.headers.get("Content-Type") || "").includes("text/html");
            if (!html || response.status >= 500) {
                if (init) {
                    // The form reached the server and failed there; sending it again could do it twice.
                    window.gymToast(t("Something went wrong ({0}). Try again.", response.status));
                    return;
                }
                throw new Error("not a page");
            }
            const text = await response.text();
            if (!isCurrent()) {
                return;
            }
            const doc = new DOMParser().parseFromString(text, "text/html");
            const samePage = finalUrl.href === location.href;
            // A change that comes back to the same page keeps the user's place on it (Phase 11), also when the page
            // comes back under a fuller address (a food deleted on /food returns to /food?date=...).
            if (keepScroll && finalUrl.pathname === location.pathname) {
                scrollY = window.scrollY;
            }
            const same = finalUrl.pathname === location.pathname && finalUrl.search === location.search;
            const swap = finalUrl.pathname === location.pathname && !same;
            await swapIn(doc, finalUrl.href, { push: push && !samePage, isBack, scrollY, side, instant, same, swap, posted: init?.method === "POST", isCurrent });
        } catch (error) {
            if (!isCurrent()) {
                return;
            }
            if (error instanceof FullLoad) {
                location.href = error.url;
            } else {
                fallback();
            }
        } finally {
            clearTimeout(slow);
            if (isCurrent()) {
                root.classList.remove("is-navigating");
            }
        }
    }

    window.gymNav = (href, options) => go(new URL(href, location.href), options);

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
        go(url, { side: segmentSide(a) });
    });

    // Plain POST forms (no htmx): submit with fetch, follow the redirect, swap the result. The form's buttons stay
    // disabled until the request ends, so it can't be sent twice.
    document.addEventListener("submit", (event) => {
        const form = event.target;
        if (event.defaultPrevented || !(form instanceof HTMLFormElement) || form.method.toLowerCase() !== "post") {
            return;
        }
        if (form.hasAttribute("hx-post") || form.target || form.enctype === "multipart/form-data") {
            return;
        }
        const action = new URL(form.action, location.href);
        if (action.origin !== location.origin) {
            return;
        }
        event.preventDefault();
        dropPrefetch();
        const body = new URLSearchParams(new FormData(form, event.submitter));
        const buttons = form.querySelectorAll("button[type=submit], button:not([type])");
        buttons.forEach((b) => { b.disabled = true; });
        go(action, {
            init: { method: "POST", body },
            keepScroll: true,
            // A form that changes the whole look (the theme) comes back without a page transition.
            instant: form.hasAttribute("data-instant"),
            // No answer: the change may or may not have been saved, so say so instead of sending it again.
            fallback: () => window.gymToast(t("Couldn't load the page. That change may have been saved. Check before trying again.")),
        }).finally(() => {
            // A swapped-out form is gone anyway; one still on screen (an error) must be usable again.
            form.classList.remove("is-busy");
            buttons.forEach((b) => { b.disabled = false; });
        });
    });

    window.addEventListener("popstate", (event) => {
        const state = event.state || {};
        // After an iOS edge swipe the browser has already animated the page away; a second slide would replay it.
        go(new URL(location.href), { push: false, isBack: true, scrollY: state.scrollY || 0, instant: !!event.hasUAVisualTransition, fallback: () => location.reload() });
    });
})();
