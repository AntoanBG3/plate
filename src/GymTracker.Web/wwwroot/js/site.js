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

    // Settings that apply as soon as they change. A theme change shows at once, then saves: a crossfade between the
    // two themes passed through a muddy grey with doubled text (Phase 11), so there is none.
    document.addEventListener("change", (event) => {
        const form = event.target.closest("form[data-autosubmit]");
        if (!form) {
            return;
        }
        const theme = event.target.dataset.themePreview;
        if (theme) {
            document.documentElement.dataset.theme = theme;
        }
        form.requestSubmit();
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

    // ---- exercise picker: tick several, add them in the order ticked (Phase 11) ----

    document.addEventListener("change", (event) => {
        const box = event.target.closest("form[data-pick-multi] input[name='exerciseIds']");
        if (!box) {
            return;
        }
        const form = box.form;
        const order = (form.dataset.order || "").split(",").filter((id) => id && id !== box.value);
        if (box.checked) {
            order.push(box.value);
        }
        form.dataset.order = order.join(",");
        const button = form.querySelector("[data-pick-submit]");
        button.disabled = order.length === 0;
        button.textContent = order.length ? t("Add {0}", order.length) : t("Add");
    });

    // Capture phase, before nav.js reads the form: the ticked boxes are sent as hidden fields in the order ticked.
    document.addEventListener("submit", (event) => {
        const form = event.target.closest("form[data-pick-multi]");
        if (!form) {
            return;
        }
        form.querySelectorAll("input[data-pick-order]").forEach((input) => input.remove());
        const boxes = form.querySelectorAll("input[name='exerciseIds']:not([data-pick-order])");
        boxes.forEach((box) => { box.disabled = true; });
        (form.dataset.order || "").split(",").filter(Boolean).forEach((id) => {
            const input = document.createElement("input");
            input.type = "hidden";
            input.name = "exerciseIds";
            input.value = id;
            input.dataset.pickOrder = "";
            form.appendChild(input);
        });
        // Usable again if the page stays (no connection).
        setTimeout(() => boxes.forEach((box) => { box.disabled = false; }), 0);
    }, true);

    // "Create a custom exercise" from a search with no match takes the search text as the name.
    document.addEventListener("click", (event) => {
        const link = event.target.closest("[data-create-exercise]");
        if (!link) {
            return;
        }
        const search = document.querySelector("[data-filter]");
        const url = new URL(link.getAttribute("href"), location.href);
        url.searchParams.set("name", search ? search.value.trim() : "");
        link.setAttribute("href", url.pathname + url.search);
    }, true);

    // ---- reorder mode: drag a handle, or use the up and down buttons; the hidden ids are sent in screen order ----

    let dragging = null;
    document.addEventListener("pointerdown", (event) => {
        const handle = event.target.closest("[data-reorder] .drag-handle");
        if (!handle) {
            return;
        }
        event.preventDefault();
        const item = handle.closest("li");
        handle.setPointerCapture(event.pointerId);
        dragging = { item, list: item.parentElement, y: event.clientY };
        item.classList.add("is-dragging");
    });
    document.addEventListener("pointermove", (event) => {
        if (!dragging) {
            return;
        }
        const { item, list } = dragging;
        const follow = () => { item.style.transform = `translateY(${event.clientY - dragging.y}px)`; };
        follow();
        // Swap with a neighbour while the item's middle is past the neighbour's, keeping it under the finger.
        for (let moved = true; moved;) {
            moved = false;
            const mid = item.getBoundingClientRect().top + item.offsetHeight / 2;
            const prev = item.previousElementSibling;
            const next = item.nextElementSibling;
            if (prev && mid < prev.getBoundingClientRect().top + prev.offsetHeight / 2) {
                list.insertBefore(item, prev);
                dragging.y -= prev.offsetHeight;
                moved = true;
            } else if (next && mid > next.getBoundingClientRect().top + next.offsetHeight / 2) {
                list.insertBefore(next, item);
                dragging.y += next.offsetHeight;
                moved = true;
            }
            follow();
        }
    });
    for (const name of ["pointerup", "pointercancel"]) {
        document.addEventListener(name, () => {
            if (dragging) {
                dragging.item.style.transform = "";
                dragging.item.classList.remove("is-dragging");
                dragging = null;
            }
        });
    }
    document.addEventListener("click", (event) => {
        const button = event.target.closest("[data-reorder] [data-move]");
        if (!button) {
            return;
        }
        const item = button.closest("li");
        if (button.dataset.move === "-1" && item.previousElementSibling) {
            item.parentElement.insertBefore(item, item.previousElementSibling);
        } else if (button.dataset.move === "1" && item.nextElementSibling) {
            item.parentElement.insertBefore(item.nextElementSibling, item);
        }
        button.focus();
    });

    // ---- Change food on the confirmation list: search, then the picked food goes on that row ----

    let changeRow = null;
    document.addEventListener("click", (event) => {
        const open = event.target.closest("[data-change-food]");
        if (open) {
            changeRow = document.querySelector(`.result[data-row="${open.dataset.changeFood}"]`);
            const sheet = document.getElementById("change-food-sheet");
            const search = sheet.querySelector("[data-change-search]");
            search.value = open.dataset.query || "";
            sheet.querySelector("#change-food-results").replaceChildren();
            window.gymSheet.show(sheet);
            if (search.value && window.htmx) {
                window.htmx.trigger(search, "search");
            }
            return;
        }
        const pick = event.target.closest("[data-candidate]");
        if (!pick || !changeRow) {
            return;
        }
        const i = changeRow.dataset.row;
        const set = (field, value) => {
            const input = changeRow.querySelector(`[name="items[${i}].${field}"]`);
            if (input) {
                input.value = value || "";
            }
        };
        set("FoodItemId", pick.dataset.candidate);
        set("Kcal", pick.dataset.kcal);
        set("Protein", pick.dataset.protein);
        set("Carbs", pick.dataset.carbs);
        set("Fat", pick.dataset.fat);
        set("Fiber", pick.dataset.fiber);
        set("SaturatedFat", pick.dataset.satfat);
        set("MatchModel", "");
        changeRow.dataset.per100 = `${pick.dataset.kcal},${pick.dataset.protein}`;
        const using = changeRow.querySelector("[data-using]");
        using.textContent = t("Using: {0}", pick.dataset.name);
        using.hidden = false;
        changeRow.querySelectorAll("[data-note]").forEach((note) => note.remove());
        const chip = changeRow.querySelector("[data-chip]");
        chip.className = "chip good";
        chip.textContent = t("Database");
        // The live totals beside the grams follow (the input handler above).
        changeRow.querySelector('input[name$="Grams"]').dispatchEvent(new Event("input", { bubbles: true }));
        window.gymSheet.hide();
        changeRow = null;
    });

    // A template renamed in place: its titles follow (the large title, the compact one, the tab's).
    document.body.addEventListener("gym:renamed", (event) => {
        const name = event.detail.name;
        document.querySelectorAll(".large-title, .topbar-title").forEach((el) => { el.textContent = name; });
        document.title = name + " · Gym";
    });

    // The weekly rate only applies to a cut or bulk (SPEC 10): for Maintain the row is hidden (app.css) and the field
    // disabled, so it is neither required nor sent; otherwise it is required, with a typical rate as the hint.
    const syncRate = (form) => {
        const rate = form.querySelector("[data-rate-row] input");
        const goal = form.elements.goal && form.elements.goal.value;
        if (!rate || !goal) {
            return;
        }
        rate.disabled = goal === "Maintain";
        rate.required = !rate.disabled;
        rate.placeholder = rate.dataset["hint" + goal] || "";
    };
    const syncRates = () => document.querySelectorAll("form:has([data-rate-row])").forEach(syncRate);
    document.addEventListener("change", (event) => {
        if (event.target.name === "goal" && event.target.form) {
            syncRate(event.target.form);
        }
    });
    document.addEventListener("gym:page", syncRates);
    document.body.addEventListener("htmx:afterSettle", syncRates); // the setup card redrawn from the chat
    syncRates();

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
        // A decimal field may hold a comma (the Bulgarian keypad).
        const grams = parseFloat(((row.querySelector('input[name$="Grams"], input[name="grams"]') || {}).value || "").replace(",", ".")) || 0;
        const per = row.dataset.per100.split(",").map(Number);
        const out = row.querySelector("[data-totals]");
        if (out) {
            const f = grams / 100;
            out.textContent = t("{0} kcal · {1} g protein", Math.round(per[0] * f), (per[1] * f).toFixed(1));
        }
    });
})();
