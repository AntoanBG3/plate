// Behaviour shared by the three design samples. Styling lives in each sample; this only toggles
// classes, drives the timer ring, the sheet drag and view transitions.
(function () {
    "use strict";

    const root = document.documentElement;
    const reduce = matchMedia("(prefers-reduced-motion: reduce)");
    const params = new URLSearchParams(location.search);
    if (params.get("theme")) {
        root.dataset.theme = params.get("theme");
    }
    const $ = (s, el = document) => el.querySelector(s);
    const $$ = (s, el = document) => [...el.querySelectorAll(s)];

    // ---- press feedback: iOS only applies :active with a touch listener, so drive a class ----
    const pressable = "button, .tap, a[href], [data-tab], label.tap";
    document.addEventListener("pointerdown", (e) => {
        const el = e.target.closest(pressable);
        if (!el || el.disabled) return;
        el.classList.add("is-pressed");
        const up = () => {
            el.classList.remove("is-pressed");
            removeEventListener("pointerup", up);
            removeEventListener("pointercancel", up);
        };
        addEventListener("pointerup", up);
        addEventListener("pointercancel", up);
    }, { passive: true });
    document.addEventListener("pointerleave", (e) => {
        if (e.target instanceof Element) e.target.classList?.remove("is-pressed");
    }, true);

    // ---- navigation with view transitions ----
    function show(name) {
        $$("[data-screen]").forEach((s) => { s.hidden = s.dataset.screen !== name; });
        $$("[data-tab]").forEach((t) => {
            const on = t.dataset.tab === ($(`[data-screen="${name}"]`).dataset.tabOf || name);
            t.classList.toggle("is-active", on);
            t.setAttribute("aria-current", on ? "page" : "false");
        });
        scrollTo(0, 0);
        observeTitle();
        reveal($(`[data-screen="${name}"]`));
        syncTimer();
    }

    function go(name, dir) {
        root.dataset.nav = dir;
        if (!document.startViewTransition || reduce.matches && dir !== "tab") {
            show(name);
            return;
        }
        document.startViewTransition(() => show(name));
    }

    document.addEventListener("click", (e) => {
        const link = e.target.closest("[data-go]");
        if (link) {
            e.preventDefault();
            go(link.dataset.go, link.dataset.dir || "tab");
        }
    });

    // ---- large title collapses into the compact bar (IntersectionObserver, no scroll handler) ----
    let titleObserver;
    function observeTitle() {
        titleObserver?.disconnect();
        const screen = $("[data-screen]:not([hidden])");
        const sentinel = screen && $("[data-title-sentinel]", screen);
        const bar = $("[data-topbar]");
        if (!sentinel || !bar) return;
        titleObserver = new IntersectionObserver(([entry]) => {
            bar.classList.toggle("is-compact", !entry.isIntersecting);
        }, { rootMargin: `-${parseInt(getComputedStyle(bar).height) || 52}px 0px 0px 0px` });
        titleObserver.observe(sentinel);
        $("[data-topbar-title]").textContent = sentinel.dataset.titleSentinel;
        const back = $("[data-topbar-back]");
        if (back) back.hidden = !screen.dataset.back;
        const trailing = $("[data-topbar-trailing]");
        if (trailing) trailing.hidden = !screen.dataset.trailing;
    }

    // ---- numbers count up and rings draw in, first appearance only ----
    const seen = new Set();
    function reveal(screen) {
        if (!screen || seen.has(screen.dataset.screen)) return;
        seen.add(screen.dataset.screen);
        const animate = !reduce.matches;
        $$("[data-count]", screen).forEach((el) => countTo(el, Number(el.dataset.count), animate ? 900 : 0));
        $$("[data-progress]", screen).forEach((el) => {
            const value = Math.min(1, Number(el.dataset.progress));
            const len = el.getTotalLength ? el.getTotalLength() : 0;
            el.style.strokeDasharray = `${len}`;
            if (!animate) {
                el.style.strokeDashoffset = `${len * (1 - value)}`;
                return;
            }
            el.animate([{ strokeDashoffset: len }, { strokeDashoffset: len * (1 - value) }],
                { duration: 1100, easing: "cubic-bezier(.2,.8,.2,1)", fill: "forwards", delay: 120 });
        });
        $$("[data-draw]", screen).forEach((el) => {
            const len = el.getTotalLength();
            el.style.strokeDasharray = `${len}`;
            if (animate) {
                el.animate([{ strokeDashoffset: len }, { strokeDashoffset: 0 }],
                    { duration: 900, easing: "cubic-bezier(.3,.7,.2,1)", fill: "forwards", delay: 300 });
            }
        });
    }

    function countTo(el, to, ms) {
        const from = Number(el.dataset.from || 0);
        const decimals = (String(el.dataset.count).split(".")[1] || "").length;
        if (!ms) {
            el.textContent = to.toFixed(decimals);
            return;
        }
        const t0 = performance.now();
        const step = (t) => {
            const p = Math.min(1, (t - t0) / ms);
            const eased = 1 - Math.pow(1 - p, 4);
            el.textContent = (from + (to - from) * eased).toFixed(decimals);
            if (p < 1) requestAnimationFrame(step);
        };
        requestAnimationFrame(step);
    }

    // ---- set rows: done, add, swipe to delete ----
    document.addEventListener("click", (e) => {
        const done = e.target.closest("[data-done]");
        if (done) {
            const row = done.closest("[data-set]");
            const wasDone = row.classList.contains("is-done");
            row.classList.toggle("is-done", !wasDone);
            done.setAttribute("aria-pressed", String(!wasDone));
            $$("[data-suggested]", row).forEach((input) => {
                if (!input.value) input.value = input.placeholder;
            });
            if (!wasDone && !row.classList.contains("is-warmup")) {
                const ex = row.closest("[data-rest]");
                startTimer(Number(ex?.dataset.rest || 120));
            }
            return;
        }
        const add = e.target.closest("[data-add-set]");
        if (add) {
            const list = add.closest("[data-exercise]").querySelector("[data-rows]");
            const last = $$("[data-set]:not(.is-warmup)", list).pop();
            const row = last.cloneNode(true);
            row.classList.remove("is-done", "is-open");
            row.querySelector(".set-content")?.style.removeProperty("transform");
            const n = $$("[data-set]:not(.is-warmup)", list).length + 1;
            $$("[data-n]", row).forEach((el) => { el.textContent = n; });
            list.append(row);
            if (!reduce.matches) {
                const h = row.offsetHeight;
                row.animate([{ height: "0px", opacity: 0 }, { height: `${h}px`, opacity: 1 }],
                    { duration: 340, easing: getComputedStyle(root).getPropertyValue("--ease-snappy").trim() || "ease-out" });
            }
            return;
        }
        const del = e.target.closest("[data-delete-set]");
        if (del) {
            const row = del.closest("[data-set]");
            collapse(row);
            return;
        }
        if (!e.target.closest("[data-set].is-open")) {
            $$("[data-set].is-open").forEach(closeSwipe);
        }
    });

    function collapse(el) {
        if (reduce.matches) {
            el.remove();
            return;
        }
        const h = el.offsetHeight;
        el.style.overflow = "hidden";
        el.animate([{ height: `${h}px`, opacity: 1 }, { height: "0px", opacity: 0 }],
            { duration: 280, easing: "cubic-bezier(.4,0,.2,1)" }).finished.then(() => el.remove());
    }

    function closeSwipe(row) {
        row.classList.remove("is-open");
        const c = $(".set-content", row);
        if (c) c.style.transform = "";
    }

    // Horizontal drag on a row reveals Delete; vertical drags still scroll (touch-action: pan-y).
    let drag = null;
    document.addEventListener("pointerdown", (e) => {
        const content = e.target.closest("[data-set] .set-content");
        if (!content || e.target.closest("input, button")) return;
        drag = { content, row: content.closest("[data-set]"), x: e.clientX, y: e.clientY, dx: 0, active: false };
    });
    document.addEventListener("pointermove", (e) => {
        if (!drag) return;
        const dx = e.clientX - drag.x;
        const dy = e.clientY - drag.y;
        if (!drag.active) {
            if (Math.abs(dy) > 8) { drag = null; return; }
            if (Math.abs(dx) < 8) return;
            drag.active = true;
            drag.row.classList.add("is-swiping");
            drag.content.style.transition = "none";
        }
        const base = drag.row.classList.contains("is-open") ? -88 : 0;
        drag.dx = Math.min(0, Math.max(-120, base + dx));
        drag.content.style.transform = `translateX(${drag.dx}px)`;
    });
    addEventListener("pointerup", () => {
        if (!drag) return;
        if (drag.active) {
            drag.content.style.transition = "";
            drag.row.classList.remove("is-swiping");
            const open = drag.dx < -44;
            drag.row.classList.toggle("is-open", open);
            drag.content.style.transform = open ? "translateX(-88px)" : "";
        }
        drag = null;
    });

    // ---- context menu for exercise actions ----
    document.addEventListener("click", (e) => {
        const trigger = e.target.closest("[data-menu-trigger]");
        const open = $(".menu.is-open");
        if (trigger) {
            const menu = trigger.parentElement.querySelector(".menu");
            if (open && open !== menu) open.classList.remove("is-open");
            menu.classList.toggle("is-open");
            trigger.setAttribute("aria-expanded", String(menu.classList.contains("is-open")));
            return;
        }
        if (open && !e.target.closest(".menu")) open.classList.remove("is-open");
    });

    // ---- rest timer: end time is the truth, the ring and digits follow it ----
    let timer = null;
    let frame = 0;
    const pill = () => $("[data-timer-pill]");

    function startTimer(seconds) {
        timer = { end: Date.now() + seconds * 1000, total: seconds };
        syncTimer();
    }

    function syncTimer() {
        const p = pill();
        if (!p) return;
        const inSession = !$('[data-screen="session"]').hidden;
        p.classList.toggle("is-visible", !!timer && inSession);
        p.setAttribute("aria-hidden", String(!(timer && inSession)));
        cancelAnimationFrame(frame);
        if (timer) frame = requestAnimationFrame(tick);
    }

    function fmt(s) {
        return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
    }

    function tick() {
        if (!timer) return;
        const leftMs = Math.max(0, timer.end - Date.now());
        const left = Math.ceil(leftMs / 1000);
        $$("[data-timer-text]").forEach((el) => { el.textContent = fmt(left); });
        $$("[data-timer-ring]").forEach((ring) => {
            const len = ring.getTotalLength();
            ring.style.strokeDasharray = `${len}`;
            ring.style.strokeDashoffset = `${len * (1 - leftMs / (timer.total * 1000))}`;
        });
        $$("[data-timer-bar]").forEach((bar) => {
            bar.style.transform = `scaleX(${leftMs / (timer.total * 1000)})`;
        });
        const finished = leftMs === 0;
        document.body.classList.toggle("timer-done", finished);
        if (finished && !timer.announced) {
            timer.announced = true;
            const live = $("[data-timer-live]");
            if (live) live.textContent = "Rest over";
        }
        frame = requestAnimationFrame(tick);
    }

    document.addEventListener("click", (e) => {
        if (e.target.closest("[data-timer-add]") && timer) {
            timer.end = Math.max(Date.now(), timer.end) + 30000;
            timer.total = Math.max(timer.total, Math.ceil((timer.end - Date.now()) / 1000));
            timer.announced = false;
        } else if (e.target.closest("[data-timer-skip]")) {
            timer = null;
            closeSheet();
            syncTimer();
            document.body.classList.remove("timer-done");
        } else if (e.target.closest("[data-timer-pill]")) {
            openSheet();
        }
    });

    // ---- bottom sheet: gentle spring up, page behind scales down, drag down to dismiss ----
    const sheet = () => $("[data-sheet]");
    function openSheet() {
        const s = sheet();
        if (!s) return;
        s.hidden = false;
        s.getBoundingClientRect();
        document.body.classList.add("sheet-open");
        s.querySelector(".sheet-panel").style.transform = "";
    }
    function closeSheet() {
        const s = sheet();
        if (!s || s.hidden) return;
        document.body.classList.remove("sheet-open");
        const panel = s.querySelector(".sheet-panel");
        panel.style.transform = "";
        const done = () => { if (!document.body.classList.contains("sheet-open")) s.hidden = true; };
        reduce.matches ? done() : setTimeout(done, 420);
    }

    document.addEventListener("click", (e) => {
        if (e.target.closest("[data-sheet-close]") || e.target.matches("[data-sheet] .sheet-scrim")) closeSheet();
    });
    document.addEventListener("keydown", (e) => {
        if (e.key === "Escape") closeSheet();
    });

    let sheetDrag = null;
    document.addEventListener("pointerdown", (e) => {
        const panel = e.target.closest("[data-sheet] .sheet-panel");
        if (!panel || e.target.closest("button")) return;
        sheetDrag = { panel, y: e.clientY, t: performance.now(), dy: 0, v: 0 };
        panel.style.transition = "none";
        panel.setPointerCapture(e.pointerId);
    });
    document.addEventListener("pointermove", (e) => {
        if (!sheetDrag) return;
        const now = performance.now();
        const dy = e.clientY - sheetDrag.y;
        sheetDrag.v = (dy - sheetDrag.dy) / Math.max(1, now - sheetDrag.t);
        sheetDrag.t = now;
        // Rubber-band upwards, follow the finger downwards.
        sheetDrag.dy = dy;
        const y = dy < 0 ? -Math.sqrt(-dy) * 2 : dy;
        sheetDrag.panel.style.transform = `translateY(${y}px)`;
        const p = Math.max(0, Math.min(1, dy / sheetDrag.panel.offsetHeight));
        document.body.style.setProperty("--sheet-progress", String(1 - p));
    });
    document.addEventListener("pointerup", () => {
        if (!sheetDrag) return;
        const { panel, dy, v } = sheetDrag;
        panel.style.transition = "";
        document.body.style.removeProperty("--sheet-progress");
        sheetDrag = null;
        if (dy > panel.offsetHeight * 0.3 || v > 0.6) {
            panel.style.transform = `translateY(100%)`;
            closeSheet();
        } else {
            panel.style.transform = "";
        }
    });

    // ---- Today: save weight, number animates to the new trend ----
    document.addEventListener("submit", (e) => {
        const form = e.target.closest("[data-weight-form]");
        if (!form) return;
        e.preventDefault();
        const input = form.querySelector("input");
        const kg = parseFloat(input.value.replace(",", "."));
        if (!kg) return;
        const trend = $("[data-trend]");
        const old = Number(trend.textContent);
        const next = Math.round((old + (kg - old) * 0.1) * 10) / 10;
        trend.dataset.from = String(old);
        trend.dataset.count = next.toFixed(1);
        countTo(trend, next, reduce.matches ? 0 : 600);
        form.classList.add("is-saved");
        const button = form.querySelector("button");
        button.textContent = "Saved";
        setTimeout(() => { form.classList.remove("is-saved"); button.textContent = "Update"; }, 1600);
    });

    // ---- start ----
    const start = params.get("screen") || "today";
    show(start);
    if (params.get("timer") !== "off") {
        timer = { end: Date.now() + 84000, total: 180 };
        syncTimer();
    }
    if (params.get("sheet") === "open") openSheet();
})();
