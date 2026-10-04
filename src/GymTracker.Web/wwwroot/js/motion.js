// Motion and small interactions shared by every page: tap feedback, page transition direction, the large title
// collapsing into the top bar, numbers and rings that animate on first sight, rows that animate in and out,
// bottom sheets, context menus and swipe to delete. Only transform and opacity are animated on lists.
(function () {
    "use strict";

    const root = document.documentElement;
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)");
    const $$ = (sel, el = document) => Array.from(el.querySelectorAll(sel));
    const snappy = () => getComputedStyle(root).getPropertyValue("--ease-snappy").trim() || "ease-out";
    const gentle = () => getComputedStyle(root).getPropertyValue("--ease-gentle").trim() || "ease-out";

    function store(kind, key, value) {
        try {
            if (value === undefined) {
                return window[kind].getItem(key);
            }
            window[kind].setItem(key, value);
        } catch {
            return null;
        }
        return null;
    }

    // ---- tap feedback (iOS only applies :active with a touch listener, so drive a class) ----

    const pressable = ".btn, .tap, .tab, .icon-btn, .done-btn, .segment, .rest-btn, .topbar-back, .topbar-action, .ex-actions button, .row-link";
    document.addEventListener("pointerdown", (event) => {
        const el = event.target.closest(pressable);
        if (!el || el.disabled) {
            return;
        }
        el.classList.add("is-pressed");
        const release = () => {
            el.classList.remove("is-pressed");
            window.removeEventListener("pointerup", release);
            window.removeEventListener("pointercancel", release);
            window.removeEventListener("scroll", release, true);
        };
        window.addEventListener("pointerup", release);
        window.addEventListener("pointercancel", release);
        window.addEventListener("scroll", release, true);
    }, { passive: true });

    // ---- page transitions: tabs slide sideways in tab-bar order, deeper pages push, shallower pages pop ----

    function remember() {
        store("sessionStorage", "gym.nav", JSON.stringify({ depth: root.dataset.depth, tab: root.dataset.tab }));
    }
    window.addEventListener("pageswap", remember);
    window.addEventListener("pagehide", remember);
    window.addEventListener("pagereveal", (event) => {
        if (!event.viewTransition) {
            return;
        }
        let from = null;
        try {
            from = JSON.parse(store("sessionStorage", "gym.nav") || "null");
        } catch {
            from = null;
        }
        const to = { depth: Number(root.dataset.depth || 0), tab: root.dataset.tab };
        let nav = "tab";
        if (from && from.tab === to.tab && Number(from.depth) !== to.depth) {
            nav = to.depth > Number(from.depth) ? "forward" : "back";
        } else if (from && from.tab !== to.tab) {
            const tabs = Array.from(document.querySelectorAll(".tabbar .tab"), (t) => t.getAttribute("href"));
            const a = tabs.indexOf("/" + from.tab);
            const b = tabs.indexOf("/" + to.tab);
            if (a >= 0 && b >= 0) {
                nav = b > a ? "next" : "prev";
            }
        }
        root.dataset.nav = reduce.matches ? "tab" : nav;
        event.viewTransition.finished.finally(() => { delete root.dataset.nav; });
    });

    // ---- large title collapses into the compact bar ----

    let titleObserver = null;
    function bindLargeTitle() {
        if (titleObserver) {
            titleObserver.disconnect();
            titleObserver = null;
        }
        const bar = document.querySelector("[data-topbar]");
        const title = document.querySelector(".large-title");
        if (!bar) {
            return;
        }
        bar.classList.remove("is-compact", "always-title");
        if (title && "IntersectionObserver" in window) {
            titleObserver = new IntersectionObserver(([entry]) => {
                bar.classList.toggle("is-compact", !entry.isIntersecting);
            }, { rootMargin: `-${bar.offsetHeight}px 0px 0px 0px` });
            titleObserver.observe(title);
        } else if (!title) {
            bar.classList.add("is-compact", "always-title");
        }
    }
    bindLargeTitle();

    // ---- numbers count up and rings draw in, the first time they are seen this session ----

    function seenBefore(key) {
        const seen = store("sessionStorage", "gym.seen") || "";
        if (seen.split("|").includes(key)) {
            return true;
        }
        store("sessionStorage", "gym.seen", seen + "|" + key);
        return false;
    }

    function countTo(el, to, ms) {
        const from = Number(el.dataset.from || 0);
        const decimals = (String(el.dataset.count).split(".")[1] || "").length;
        const fmt = (v) => v.toFixed(decimals);
        if (!ms) {
            el.textContent = fmt(to);
            return;
        }
        const start = performance.now();
        const step = (now) => {
            const p = Math.min(1, (now - start) / ms);
            el.textContent = fmt(from + (to - from) * (1 - Math.pow(1 - p, 4)));
            if (p < 1) {
                requestAnimationFrame(step);
            }
        };
        requestAnimationFrame(step);
    }
    window.gymCountTo = countTo;

    function reveal(scope) {
        const key = "reveal:" + location.pathname;
        const animate = !reduce.matches && !seenBefore(key);
        $$("[data-count]", scope).forEach((el) => {
            if (animate) {
                el.dataset.from = el.dataset.from || "0";
                countTo(el, Number(el.dataset.count), 900);
            }
        });
        $$("[data-progress]", scope).forEach((el) => {
            const value = Math.max(0, Math.min(1, Number(el.dataset.progress)));
            const len = el.getTotalLength();
            el.style.strokeDasharray = `${len} ${len}`;
            el.style.strokeDashoffset = `${len * (1 - value)}`;
            if (value === 0) {
                el.style.opacity = "0";
            }
            if (animate && value > 0) {
                el.animate([{ strokeDashoffset: len }, { strokeDashoffset: len * (1 - value) }],
                    { duration: 1100, delay: 120, easing: "cubic-bezier(.2,.8,.2,1)", fill: "backwards" });
            }
        });
        $$("[data-draw]", scope).forEach((el) => {
            if (!animate) {
                return;
            }
            const len = el.getTotalLength();
            el.animate([{ strokeDasharray: `${len} ${len}`, strokeDashoffset: len }, { strokeDasharray: `${len} ${len}`, strokeDashoffset: 0 }],
                { duration: 900, delay: 250, easing: "cubic-bezier(.3,.7,.2,1)", fill: "backwards" });
        });
    }
    reveal(document);

    // After a soft navigation (nav.js) the new page needs its own title binding and first-sight animations.
    document.addEventListener("gym:page", (event) => {
        bindLargeTitle();
        const main = document.getElementById("main");
        reveal(main);

        // New content rises in, first eight blocks staggered. Not on Back, which should feel like returning, nor on
        // a sideways slide, which already moves the whole page.
        if (reduce.matches || (event.detail.nav !== "forward" && event.detail.nav !== "tab")) {
            return;
        }
        Array.from(main.children).slice(0, 8).forEach((el, i) => el.style.setProperty("--i", String(i)));
        main.classList.add("is-entering");
        setTimeout(() => main.classList.remove("is-entering"), 600);
    });

    // ---- things that appear or leave: htmx rows, list items ----

    function appear(el) {
        if (reduce.matches || !(el instanceof HTMLElement)) {
            return;
        }
        const h = el.offsetHeight;
        el.animate([{ height: "0px", opacity: 0, overflow: "hidden" }, { height: `${h}px`, opacity: 1, overflow: "hidden" }],
            { duration: 340, easing: snappy() });
    }
    window.gymAppear = appear;

    function collapse(el) {
        if (reduce.matches || !(el instanceof HTMLElement)) {
            return Promise.resolve();
        }
        const h = el.offsetHeight;
        return el.animate([{ height: `${h}px`, opacity: 1, overflow: "hidden" }, { height: "0px", opacity: 0, overflow: "hidden" }],
            { duration: 260, easing: "cubic-bezier(.4,0,.2,1)", fill: "forwards" }).finished;
    }
    window.gymCollapse = collapse;

    document.addEventListener("htmx:afterSwap", (event) => {
        const swap = event.detail.requestConfig && event.detail.requestConfig.elt && event.detail.requestConfig.elt.getAttribute("hx-swap");
        if (swap && swap.startsWith("beforeend")) {
            const target = event.detail.target;
            appear(target.lastElementChild);
        }
    });

    // Rows deleted through htmx collapse during the swap delay (hx-swap="outerHTML swap:260ms").
    document.addEventListener("htmx:beforeSwap", (event) => {
        if (event.detail.xhr.status === 200 && event.detail.serverResponse.trim() === "" && event.detail.target.matches(".set-row, .row, li")) {
            collapse(event.detail.target);
        }
    });

    // ---- bottom sheets: gentle spring up, page behind scales down, drag down to dismiss ----

    let openSheet = null;
    let lastFocus = null;

    function showSheet(sheet) {
        if (!sheet) {
            return;
        }
        lastFocus = document.activeElement;
        sheet.hidden = false;
        sheet.getBoundingClientRect();
        document.body.classList.add("sheet-open");
        openSheet = sheet;
        const panel = sheet.querySelector(".sheet-panel");
        panel.style.transform = "";
        const focusable = panel.querySelector("[autofocus], button, input, a[href]");
        if (focusable) {
            setTimeout(() => focusable.focus({ preventScroll: true }), 50);
        }
        sheet.dispatchEvent(new CustomEvent("sheet:open"));
    }

    function hideSheet() {
        const sheet = openSheet;
        if (!sheet) {
            return;
        }
        openSheet = null;
        document.body.classList.remove("sheet-open");
        sheet.querySelector(".sheet-panel").style.transform = "";
        sheet.dispatchEvent(new CustomEvent("sheet:close"));
        const done = () => {
            if (openSheet !== sheet) {
                sheet.hidden = true;
            }
        };
        reduce.matches ? done() : setTimeout(done, 440);
        if (lastFocus && lastFocus.focus) {
            lastFocus.focus({ preventScroll: true });
        }
    }
    window.gymSheet = { show: showSheet, hide: hideSheet };

    document.addEventListener("click", (event) => {
        const opener = event.target.closest("[data-sheet-open]");
        if (opener) {
            event.preventDefault();
            closeMenus();
            showSheet(document.getElementById(opener.dataset.sheetOpen));
            return;
        }
        if (event.target.closest("[data-sheet-close]") || event.target.classList.contains("sheet-scrim")) {
            hideSheet();
        }
    });
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            hideSheet();
            closeMenus();
        }
    });

    let drag = null;
    document.addEventListener("pointerdown", (event) => {
        const handle = event.target.closest(".sheet-grabber, .sheet-head, .sheet-drag");
        const panel = handle && handle.closest(".sheet-panel");
        if (!panel || event.target.closest("button, a, input")) {
            return;
        }
        drag = { panel, y: event.clientY, t: performance.now(), dy: 0, v: 0 };
        panel.style.transition = "none";
        panel.setPointerCapture(event.pointerId);
    });
    document.addEventListener("pointermove", (event) => {
        if (!drag) {
            return;
        }
        const now = performance.now();
        const dy = event.clientY - drag.y;
        drag.v = (dy - drag.dy) / Math.max(1, now - drag.t);
        drag.t = now;
        drag.dy = dy;
        const y = dy < 0 ? -Math.sqrt(-dy) * 2 : dy;
        drag.panel.style.transform = `translateY(${y}px)`;
        document.body.style.setProperty("--sheet-progress", String(1 - Math.max(0, Math.min(1, dy / drag.panel.offsetHeight))));
    });
    document.addEventListener("pointerup", () => {
        if (!drag) {
            return;
        }
        const { panel, dy, v } = drag;
        drag = null;
        panel.style.transition = "";
        document.body.style.removeProperty("--sheet-progress");
        if (dy > panel.offsetHeight * 0.25 || v > 0.6) {
            panel.style.transform = "translateY(100%)";
            hideSheet();
        } else {
            panel.style.transform = "";
        }
    });

    // ---- context menus ----

    function closeMenus(except) {
        $$(".menu.is-open").forEach((menu) => {
            if (menu !== except) {
                menu.classList.remove("is-open");
                const trigger = menu.parentElement.querySelector("[data-menu-trigger]");
                if (trigger) {
                    trigger.setAttribute("aria-expanded", "false");
                }
            }
        });
    }
    document.addEventListener("click", (event) => {
        const trigger = event.target.closest("[data-menu-trigger]");
        if (trigger) {
            const menu = trigger.parentElement.querySelector(".menu");
            closeMenus(menu);
            const open = menu.classList.toggle("is-open");
            trigger.setAttribute("aria-expanded", String(open));
            if (open) {
                const first = menu.querySelector("button, a");
                if (first && event.detail === 0) {
                    first.focus();
                }
            }
            return;
        }
        if (!event.target.closest(".menu")) {
            closeMenus();
        }
    });

    // ---- swipe a logged set left to reveal Delete; vertical drags still scroll ----

    let swipe = null;
    document.addEventListener("pointerdown", (event) => {
        const content = event.target.closest(".set-row.done .set-content");
        if (!content || event.target.closest("input, button")) {
            return;
        }
        swipe = { content, row: content.closest(".set-row"), x: event.clientX, y: event.clientY, dx: 0, active: false };
    });
    document.addEventListener("pointermove", (event) => {
        if (!swipe) {
            return;
        }
        const dx = event.clientX - swipe.x;
        const dy = event.clientY - swipe.y;
        if (!swipe.active) {
            if (Math.abs(dy) > 8) {
                swipe = null;
                return;
            }
            if (Math.abs(dx) < 8) {
                return;
            }
            swipe.active = true;
            swipe.row.classList.add("is-swiping");
            swipe.content.style.transition = "none";
        }
        const base = swipe.row.classList.contains("is-open") ? -88 : 0;
        swipe.dx = Math.min(0, Math.max(-120, base + dx));
        swipe.content.style.transform = `translateX(${swipe.dx}px)`;
    });
    window.addEventListener("pointerup", () => {
        if (!swipe) {
            return;
        }
        if (swipe.active) {
            const open = swipe.dx < -44;
            swipe.content.style.transition = "";
            swipe.row.classList.remove("is-swiping");
            swipe.row.classList.toggle("is-open", open);
            swipe.content.style.transform = open ? "translateX(-88px)" : "";
        }
        swipe = null;
    });
    document.addEventListener("click", (event) => {
        if (!event.target.closest(".set-row.is-open")) {
            $$(".set-row.is-open").forEach((row) => {
                row.classList.remove("is-open");
                const content = row.querySelector(".set-content");
                if (content) {
                    content.style.transform = "";
                }
            });
        }
    });

    // ---- instant filter for long lists (exercise picker) ----

    document.addEventListener("input", (event) => {
        const input = event.target.closest("[data-filter]");
        if (!input) {
            return;
        }
        const scope = document.querySelector(input.dataset.filter);
        const q = input.value.trim().toLocaleLowerCase();
        let any = false;
        $$("[data-filter-group]", scope).forEach((group) => {
            let shown = 0;
            $$("[data-filter-item]", group).forEach((item) => {
                const match = !q || item.dataset.filterItem.toLocaleLowerCase().includes(q);
                item.hidden = !match;
                shown += match ? 1 : 0;
            });
            group.hidden = shown === 0;
            any = any || shown > 0;
        });
        const empty = scope.querySelector("[data-filter-empty]");
        if (empty) {
            empty.hidden = any;
        }
    });

    // ---- toast ----

    let toastTimer;
    // An action ({ label, url }) adds a button that POSTs to url and comes back to this page (nav.js swaps it in).
    window.gymToast = function (text, action) {
        const el = document.getElementById("toast");
        if (!el) {
            return;
        }
        const message = document.createElement("span");
        message.textContent = text;
        el.replaceChildren(message);
        if (action) {
            const form = document.createElement("form");
            form.method = "post";
            form.action = action.url;
            form.className = "toast-action";
            const field = (name) => {
                const input = document.createElement("input");
                input.type = "hidden";
                input.name = name;
                form.appendChild(input);
                return input;
            };
            const token = field("__RequestVerificationToken");
            const returnUrl = field("returnUrl");
            const button = document.createElement("button");
            button.type = "submit";
            button.textContent = action.label;
            form.appendChild(button);
            // Both are read when sent: site.js (gymToken) loads after this script, nav.js keeps the token current,
            // and the toast outlives soft navigation, so undo returns to the page it was pressed on.
            form.addEventListener("submit", () => {
                token.value = window.gymToken ? window.gymToken() : "";
                returnUrl.value = location.pathname + location.search;
                el.hidden = true;
            });
            el.appendChild(form);
        }
        el.hidden = false;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => {
            if (reduce.matches) {
                el.hidden = true;
                return;
            }
            el.animate([{ opacity: 1 }, { opacity: 0, transform: "translateY(12px)" }], { duration: 220, easing: "ease-in" })
                .finished.then(() => { el.hidden = true; });
        }, action ? 6000 : 5000);
    };

    // Undo offered by the server: after a redirect (data in #main) or by an htmx response (gym:undo event).
    function offerUndo(scope) {
        const data = scope && scope.querySelector("[data-undo-url]");
        if (data) {
            window.gymToast(data.dataset.undoText, { label: t("Undo"), url: data.dataset.undoUrl });
        }
    }
    offerUndo(document.getElementById("main"));
    document.addEventListener("gym:page", () => offerUndo(document.getElementById("main")));
    document.body.addEventListener("gym:undo", (event) => {
        window.gymToast(t(event.detail.text), { label: t("Undo"), url: event.detail.url });
    });

    // Give anything with data-spring a spring entrance the first time the page shows (used by sheets' contents).
    window.gymSpring = (el, from) => {
        if (!reduce.matches) {
            el.animate([from, { transform: "none", opacity: 1 }], { duration: 440, easing: gentle() });
        }
    };
})();
