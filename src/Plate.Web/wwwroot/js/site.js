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
        if (xhr.status === 400 && event.detail.elt.closest && event.detail.elt.closest("[data-status-near]")) {
            return; // shown under the field (the autosave handlers below)
        }
        window.gymToast(xhr.status === 400 && xhr.responseText ? xhr.responseText : t("Something went wrong ({0}). Try again.", xhr.status));
    });

    document.body.addEventListener("htmx:sendError", (event) => {
        if (quiet(event)) {
            return;
        }
        window.gymToast(t("Couldn't load the page. That change may have been saved. Check before trying again."));
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
        window.gymSheet.show(sheet, submitter || document.activeElement);
    }, true);

    // ---- forms that save on change (SPEC 7: logged sets, template exercises, Settings) ----
    // A form with data-autosave and hx-post sends itself on change (its hx-trigger). It is "dirty" from the first
    // keystroke until its request starts, "saving" while it runs and "unsaved" when that failed; the server marks the
    // form it sends back as saved. What is still dirty is sent before the page is left, and failures retry on reconnect.

    const AUTOSAVE = "form[data-autosave]";

    // A long form (Settings, data-status-near) keeps one status line and one error, shown under the card holding the
    // field that changed (or the one that is missing), not at the bottom of the page.
    const showNear = (form, field) => {
        const card = field && field.closest(".list");
        if (form.hasAttribute("data-status-near") && card) {
            card.after(form.querySelector(".autosave-status"), form.querySelector("[data-autosave-error]"));
        }
    };
    const showError = (form, field, message) => {
        const error = form.querySelector("[data-autosave-error]");
        if (!error) {
            return false;
        }
        showNear(form, field);
        error.querySelector("span").textContent = message;
        error.hidden = false;
        form.classList.remove("is-saving", "is-saved", "is-unsaved");
        form.classList.add("has-error");
        return true;
    };

    for (const name of ["input", "change"]) {
        document.addEventListener(name, (event) => {
            const form = event.target.closest(AUTOSAVE);
            if (form) {
                form.gymChanged = event.target;
                form.classList.add("is-dirty");
                form.classList.remove("is-saved", "is-unsaved");
            }
        });
    }
    document.body.addEventListener("htmx:beforeRequest", (event) => {
        const form = event.detail.elt;
        if (form.matches && form.matches(AUTOSAVE)) {
            showNear(form, form.gymChanged);
            const error = form.querySelector("[data-autosave-error]");
            if (error) {
                error.hidden = true;
            }
            form.classList.remove("is-dirty", "is-unsaved", "has-error");
            form.classList.add("is-saving");
        }
    });
    // A form that swaps nothing back (Settings) is marked saved here; the others come back from the server marked.
    document.body.addEventListener("htmx:afterRequest", (event) => {
        const form = event.detail.elt;
        if (event.detail.successful && form.matches && form.matches(AUTOSAVE) && form.getAttribute("hx-swap") === "none") {
            form.classList.remove("is-saving");
            form.classList.add("is-saved");
        }
    });
    // A field the browser refuses (a required rate left empty) stops the save; say which, under it.
    document.body.addEventListener("htmx:validation:halted", (event) => {
        const form = event.detail.elt;
        const first = event.detail.errors && event.detail.errors[0];
        if (form.matches && form.matches(AUTOSAVE) && first) {
            // The app's own words where a field has them (data-missing), else the browser's.
            showError(form, first.elt, (first.validity.valueMissing && first.elt.dataset.missing) || first.message);
        }
    });
    for (const name of ["htmx:responseError", "htmx:sendError"]) {
        document.body.addEventListener(name, (event) => {
            const form = event.detail.elt;
            if (!(form.matches && form.matches(AUTOSAVE))) {
                return;
            }
            // A refused value isn't retried on reconnect: it would only be refused again.
            const xhr = event.detail.xhr;
            if (name === "htmx:responseError" && xhr.status === 400 && showError(form, form.gymChanged, xhr.responseText)) {
                return;
            }
            form.classList.remove("is-saving");
            form.classList.add("is-unsaved");
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
            window.gymSheet.show(sheet, open);
            if (search.value && window.htmx) {
                window.htmx.trigger(search, "search");
            }
            return;
        }
        const pick = event.target.closest("[data-candidate]");
        if (!pick || !changeRow) {
            return;
        }
        setRowFood(changeRow, pick.dataset.candidate, pick.dataset);
        // Another food entirely: the cooked-or-dry question was about the old one.
        changeRow.querySelector("[data-state-choice]")?.remove();
        const notes = [];
        // A counted amount ("две закуски") follows the new food's piece, pack or serving weight, unless the grams were edited.
        const gramsInput = changeRow.querySelector('input[name$="Grams"]');
        const count = Number(changeRow.dataset.amountCount);
        if (count > 0 && Number(gramsInput.value.replace(",", ".")) === Number(changeRow.dataset.amountGrams)) {
            const of = changeRow.dataset.amountOf || "piece";
            const weight = Number(pick.dataset[of] || 0);
            if (weight > 0) {
                const grams = String(Math.round(count * weight * 10) / 10);
                gramsInput.value = grams;
                changeRow.dataset.amountGrams = grams;
                notes.push(t(of === "pack" ? "{0} × a pack of {1} g" : of === "serving" ? "{0} × a serving of {1} g" : "{0} × a piece of {1} g", String(count), String(weight)));
            } else {
                notes.push(t(of === "pack" ? "The pack size isn't known, so check the grams." : of === "serving" ? "No serving size for this food, so check the grams." : "No weight per piece for this food, so check the grams."));
            }
        }
        if (pick.dataset.estimated === "true") {
            notes.push(t("These values are estimated from the recipe, not read from a label."));
        }
        showChosenFood(changeRow, { name: pick.dataset.name, status: pick.dataset.status, kcal: pick.dataset.kcal, protein: pick.dataset.protein, notes, amountGrams: changeRow.dataset.amountGrams });
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
    // The browser's own bubble on a plain form (setup) uses the app's words too.
    document.addEventListener("invalid", (event) => {
        const field = event.target;
        if (field.dataset && field.dataset.missing && field.validity.valueMissing) {
            field.setCustomValidity(field.dataset.missing);
        }
    }, true);
    document.addEventListener("input", (event) => {
        if (event.target.dataset && event.target.dataset.missing) {
            event.target.setCustomValidity("");
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

    // A one-line text area (a food name that wraps to fit): Enter doesn't add a line break.
    document.addEventListener("keydown", (event) => {
        if (event.key === "Enter" && event.target.matches && event.target.matches("textarea[data-one-line]")) {
            event.preventDefault();
        }
    });

    // Text inputs accept both decimal separators. Browsers apply min and max only to number inputs.
    document.addEventListener("input", event => {
        const field = event.target;
        if (!field.matches('[data-label-form] input[name="grams"], [data-label-form] input[name="servingGrams"]')) return;
        const grams = Number(field.value.trim().replace(",", "."));
        field.setCustomValidity(field.value && Number.isFinite(grams) && (grams <= 0 || grams > Number(field.max))
            ? t("Grams must be above 0 and at most {0}.", field.max) : "");
    });

    // Live per-row totals on the food confirmation list and portion forms.
    document.addEventListener("input", (event) => {
        const row = event.target.closest("[data-per100]");
        if (!row) {
            return;
        }
        // A decimal field may hold a comma (the Bulgarian keypad).
        const grams = parseFloat(((row.querySelector('input[name$="Grams"], input[name="grams"]') || {}).value || "").replace(",", ".")) || 0;
        const per = row.matches("[data-row]")
            ? ["Kcal", "Protein"].map(name => Number(row.querySelector(`[name$=".${name}"]`).value.replace(",", ".")) || 0)
            : row.dataset.per100.split(",").map(Number);
        const out = row.querySelector("[data-totals]");
        if (out) {
            const f = grams / 100;
            out.textContent = t("{0} kcal · {1} g protein", Math.round(per[0] * f), (per[1] * f).toFixed(1));
        }
    });

    // Puts a food's id, values, name and state into a confirmation row's fields.
    function setRowFood(row, id, d) {
        const i = row.dataset.row;
        const set = (field, value) => {
            const input = row.querySelector(`[name="items[${i}].${field}"]`);
            if (input) {
                input.value = value || "";
            }
        };
        set("FoodItemId", id);
        set("Kcal", d.kcal);
        set("Protein", d.protein);
        set("Carbs", d.carbs);
        set("Fat", d.fat);
        set("Fiber", d.fiber);
        set("SaturatedFat", d.satfat);
        set("MatchModel", "");
        // The row is named after the food it now is, with that food's raw or cooked state.
        set("Description", d.name);
        set("Status", d.status);
    }

    // "When did you weigh it?": the same staple cooked or dry; the grams stay, the food and its values change.
    document.addEventListener("click", (event) => {
        const button = event.target.closest("[data-state-choice] [data-choice-food]");
        if (!button || button.getAttribute("aria-pressed") === "true") {
            return;
        }
        const row = button.closest(".result");
        const d = button.dataset;
        setRowFood(row, d.choiceFood, d);
        showChosenFood(row, {
            name: d.name, status: d.status, kcal: d.kcal, protein: d.protein, amountGrams: row.dataset.amountGrams, state: d.state,
            notes: [t(d.state === "cooked" ? "Counted as weighed after cooking." : "Counted as weighed before cooking.")],
        });
        row.querySelector('input[name$="Grams"]').dispatchEvent(new Event("input", { bubbles: true }));
    });

    function showChosenFood(row, match) {
        row.dataset.chosenFood = JSON.stringify(match);
        if (match.amountGrams) row.dataset.amountGrams = match.amountGrams;
        // A restored review shows the cooked-or-dry answer it was left on; another food picked has no such question.
        if (!match.state) row.querySelector("[data-state-choice]")?.remove();
        row.querySelectorAll("[data-state-choice] [data-choice-food]").forEach((b) => {
            const on = b.dataset.state === match.state;
            b.setAttribute("aria-pressed", String(on));
            b.classList.toggle("is-active", on);
        });
        const status = row.querySelector("[data-status]");
        status.textContent = match.status || "";
        status.hidden = !match.status;
        row.dataset.per100 = `${match.kcal},${match.protein}`;
        const using = row.querySelector("[data-using]");
        using.textContent = t("Database: {0}", match.name);
        using.hidden = false;
        row.querySelectorAll("[data-note]").forEach((note) => note.remove());
        // Notes about the new food (its counted amount, estimated values), kept with it so a restored review shows them too.
        const changeButton = row.querySelector("[data-change-food]");
        for (const text of match.notes || []) {
            const span = document.createElement("span");
            span.dataset.note = "";
            span.textContent = text;
            changeButton.before(span);
        }
        const chip = row.querySelector("[data-chip]");
        chip.className = "chip good";
        chip.textContent = t("Database");
    }

    // Keep edits to the last few reviews in this tab. Only the signed-in owner can load the
    // corresponding form, and each review has its own server-generated request id.
    function reviewStorageKey() {
        return "plate.food-reviews." + document.documentElement.dataset.userId;
    }

    window.gymModule("food-review-saved", root => {
        const id = root.dataset.savedReview;
        if (!id) return;
        try {
            const key = reviewStorageKey();
            const drafts = JSON.parse(sessionStorage.getItem(key));
            if (!drafts || !(id in drafts)) return;
            delete drafts[id];
            if (Object.keys(drafts).length) sessionStorage.setItem(key, JSON.stringify(drafts));
            else sessionStorage.removeItem(key);
        } catch {}
    });

    // The recipe editor (SPEC 7 Food): its rows are Check foods rows, so Change food and the cooked-or-dry choice work
    // as there. Here: the dish's values per 100 g as the rows and the cooked weight change, the typed ingredients
    // cleared once they are rows, and Enter in the food search searching rather than saving the recipe.
    window.gymModule("recipe-editor", (form) => {
        const rows = form.querySelector("#recipe-rows");
        const summary = form.querySelector("[data-recipe-summary]");
        const empty = form.querySelector("[data-recipe-empty]");
        const number = (value) => Number(String(value || "").replace(",", "."));
        function update() {
            let grams = 0;
            let kcal = 0;
            let protein = 0;
            rows.querySelectorAll(".result").forEach((row) => {
                if (!row.querySelector('input[name$=".Include"]').checked) return;
                const g = number(row.querySelector('input[name$=".Grams"]').value);
                const [k, p] = (row.dataset.per100 || "0,0").split(",").map(Number);
                if (!(g > 0)) return;
                grams += g;
                kcal += k * g / 100;
                protein += p * g / 100;
            });
            empty.hidden = rows.querySelector(".result") !== null;
            const cooked = number(form.elements.yieldGrams.value);
            const weight = cooked > 0 ? cooked : grams;
            summary.textContent = weight > 0
                ? t("{0} kcal and {1} g protein per 100 g, {2} kcal in all", String(Math.round(kcal * 100 / weight)), (protein * 100 / weight).toFixed(1), String(Math.round(kcal)))
                : "";
        }
        const onInput = () => update();
        const onSwap = (event) => {
            if (event.detail.target === rows) {
                const elt = event.detail.requestConfig?.elt;
                if (elt?.matches("[data-recipe-parse]")) form.elements.text.value = "";
                // A food picked from the search is now a row: the search is done.
                if (elt?.matches("[data-recipe-add]")) {
                    form.elements.q.value = "";
                    form.querySelector("#recipe-search-results").replaceChildren();
                }
                update();
            }
        };
        const onKey = (event) => {
            if (event.key === "Enter" && event.target.matches('input[name="q"]')) {
                event.preventDefault();
                window.htmx?.trigger(event.target, "search");
            }
        };
        form.addEventListener("input", onInput);
        form.addEventListener("change", onInput);
        form.addEventListener("keydown", onKey);
        // Another food from the Change food sheet (outside the form) or the other state changes a row's values without
        // an input event.
        const onClick = () => setTimeout(update);
        document.addEventListener("click", onClick);
        document.addEventListener("htmx:afterSwap", onSwap);
        update();
        return () => {
            document.removeEventListener("click", onClick);
            document.removeEventListener("htmx:afterSwap", onSwap);
        };
    });

    // Native Back can restore an old document without asking the server whether this review was saved.
    window.addEventListener("pageshow", event => {
        if (event.persisted && document.querySelector('[data-module="food-review"]')) location.reload();
    });

    window.gymModule("food-review", (root) => {
        const form = root.querySelector("form");
        const id = form.elements.requestId.value;
        const key = reviewStorageKey();
        const rows = Array.from(form.querySelectorAll("[data-row]"));
        const fields = row => Array.from(row.querySelectorAll("[name]"));
        function readDrafts() {
            try { return JSON.parse(sessionStorage.getItem(key)) || {}; } catch { return {}; }
        }
        const saved = readDrafts()[id];
        if (Array.isArray(saved) && saved.length === rows.length) {
            rows.forEach((row, index) => {
                const draft = saved[index];
                for (const field of fields(row)) {
                    if (!(field.name in draft.fields)) continue;
                    if (field.type === "checkbox") field.checked = draft.fields[field.name];
                    else field.value = draft.fields[field.name];
                }
                if (draft.match) showChosenFood(row, draft.match);
                row.querySelector('[name$=".Grams"]').dispatchEvent(new Event("input", { bubbles: true }));
            });
        }
        function save() {
            const drafts = readDrafts();
            delete drafts[id];
            drafts[id] = rows.map(row => ({
                fields: Object.fromEntries(fields(row).map(f => [f.name, f.type === "checkbox" ? f.checked : f.value])),
                match: row.dataset.chosenFood ? JSON.parse(row.dataset.chosenFood) : null,
            }));
            try { sessionStorage.setItem(key, JSON.stringify(Object.fromEntries(Object.entries(drafts).slice(-8)))); } catch {}
        }
        form.addEventListener("input", save);
        form.addEventListener("change", save);
    });

    // A new invite link in the admin area (English only): copy it, or hand it to the phone's share sheet.
    window.gymModule("invite-link", (root) => {
        const link = root.dataset.link;
        const copy = root.querySelector("[data-copy]");
        const share = root.querySelector("[data-share]");
        copy.addEventListener("click", () => {
            navigator.clipboard.writeText(link).then(
                () => window.gymToast("Link copied."),
                () => window.gymToast("Couldn't copy; select the link instead."));
        });
        if (navigator.share) {
            share.hidden = false;
            share.addEventListener("click", () => navigator.share({ title: "Plate", url: link }).catch(() => {}));
        }
    });
})();
