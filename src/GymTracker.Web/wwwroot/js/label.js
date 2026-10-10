// Label photos for the manual food form (SPEC 6.6): shrink the photos on the phone, look for a barcode in them, send
// them to /food/label and swap in the form the server filled. One photo reads at once; with the extra slots open, up to
// three are read together when the user asks. Only the latest read's answer is used; nothing is saved until the form is.
window.gymModule("label", (root) => {
    "use strict";

    let pending = null;
    // What the running read marked busy; a newer read that aborts it clears it, since the old one no longer can.
    let pendingBusy = null;
    let detector = null;
    const SHRINK = { maxSide: 2000, quality: 0.85, maxBytes: 2 * 1024 * 1024, steps: [[2000, 0.7], [1600, 0.7], [1200, 0.6]] };

    // Photos picked into the extra slots, by panel (the Scan tab and the form that opens under it each have one).
    const picks = new WeakMap();

    function pickedIn(panel) {
        if (!picks.has(panel)) {
            picks.set(panel, [null, null, null]);
        }
        return picks.get(panel);
    }

    function forget(panel) {
        pickedIn(panel).forEach((shot) => shot && URL.revokeObjectURL(shot.url));
        picks.delete(panel);
    }

    // Thumbnails, remove buttons and the Read button follow what is picked, the connection and a running read.
    function drawPanel(panel) {
        const list = pickedIn(panel);
        const reading = pending !== null;
        panel.querySelectorAll("[data-label-slot-index]").forEach((slot) => {
            const shot = list[Number(slot.dataset.labelSlotIndex)];
            const thumb = slot.querySelector("[data-label-thumb]");
            if (shot) {
                const img = document.createElement("img");
                img.src = shot.url;
                img.alt = "";
                thumb.replaceChildren(img);
            } else {
                thumb.replaceChildren();
            }
            slot.classList.toggle("is-filled", !!shot);
            const remove = slot.querySelector("[data-label-remove]");
            remove.hidden = !shot;
            remove.disabled = reading;
            slot.querySelector("[data-label-slot-photo]").disabled = reading;
        });
        panel.querySelector("[data-label-read]").disabled = reading || !navigator.onLine || !list.some(Boolean);
    }

    function drawPanels() {
        root.querySelectorAll("[data-label-panel]").forEach(drawPanel);
    }

    function setMore(scope, open) {
        scope.classList.toggle("is-more", open);
        scope.querySelector("[data-label-more]").setAttribute("aria-expanded", String(open));
        const panel = scope.querySelector("[data-label-panel]");
        if (!open) {
            forget(panel);
        }
        drawPanel(panel);
    }

    async function pickInto(input) {
        const file = input.files && input.files[0];
        input.value = "";
        if (!file) {
            return;
        }
        const panel = input.closest("[data-label-panel]");
        const index = Number(input.closest("[data-label-slot-index]").dataset.labelSlotIndex);
        let shot;
        try {
            shot = await window.gymImage.shrink(file, SHRINK);
        } catch {
            toast(t("Couldn't read that picture. Try another photo."));
            return;
        }
        const list = pickedIn(panel);
        if (list[index]) {
            URL.revokeObjectURL(list[index].url);
        }
        list[index] = { ...shot, url: URL.createObjectURL(shot.blob) };
        drawPanel(panel);
    }

    // The same barcode detector the scanner uses (scanner.js), made only when a photo needs it.
    async function barcodeIn(blob) {
        const api = window.BarcodeDetectionAPI;
        if (!api) {
            return null;
        }
        try {
            if (!detector) {
                api.prepareZXingModule({
                    overrides: {
                        locateFile: (path, prefix) => path.endsWith(".wasm") ? "/lib/barcode-detector/zxing_reader.wasm" : prefix + path,
                    },
                });
                detector = new api.BarcodeDetector({ formats: ["ean_13", "ean_8", "upc_a", "upc_e"] });
            }
            const codes = await detector.detect(await createImageBitmap(blob));
            return codes.length ? codes[0].rawValue : null;
        } catch {
            return null;
        }
    }

    function toast(text) {
        if (window.gymToast) {
            window.gymToast(text);
        }
    }

    function setOnline() {
        const online = navigator.onLine;
        root.querySelectorAll("[data-label-photo]").forEach((input) => { input.disabled = !online; });
        root.querySelectorAll("[data-label-button]").forEach((button) => {
            if (online) {
                button.removeAttribute("title");
            } else {
                button.title = t("Needs a connection");
            }
        });
        drawPanels();
    }

    // items: Files from the single photo button, or photos the slots already shrank. trigger: what started the read.
    async function read(items, trigger) {
        // From the Scan tab the filled form opens in the slot; otherwise it replaces the form the trigger sits in.
        const slot = trigger.matches("[data-label-start]") ? root.querySelector("[data-label-slot]") : null;
        const host = slot ? null : trigger.closest("[data-label-form]");
        const form = host && host.querySelector("form");
        const busy = slot ? root : host.querySelector("[data-label]");

        pending?.abort();
        pendingBusy?.classList.remove("is-busy");
        const controller = new AbortController();
        pending = controller;
        pendingBusy = busy;
        busy.classList.add("is-busy");
        drawPanels();
        try {
            let shots;
            try {
                // A File is shrunk here; a slot's photo was shrunk when it was picked.
                shots = await Promise.all(items.map((item) => item instanceof Blob ? window.gymImage.shrink(item, SHRINK) : item));
            } catch {
                if (pending === controller) {
                    toast(t("Couldn't read that picture. Try another photo."));
                }
                return;
            }

            const data = new FormData();
            shots.forEach((shot, i) => {
                data.append("photo", shot.blob, `label${i + 1}.jpg`);
                data.append("width", String(shot.width));
                data.append("height", String(shot.height));
            });
            data.set("date", form ? form.elements.date.value : slot.dataset.date);
            data.set("meal", form ? form.elements.meal.value : slot.dataset.meal);
            const typedName = form ? form.elements.name.value.trim() : "";
            if (typedName) {
                data.set("name", typedName);
            }
            const barcodeField = !form || !!form.querySelector("[data-barcode-input]");
            const knownBarcode = form && form.elements.barcode ? form.elements.barcode.value.trim() : "";
            data.set("barcodeField", String(barcodeField));
            // Amounts typed before the photo stay; the label's serving only fills an empty "You ate".
            for (const field of ["grams", "servingGrams"]) {
                const value = form && form.elements[field] ? form.elements[field].value.trim() : "";
                if (value) {
                    data.set(field, value.replace(",", "."));
                }
            }
            const share = form && form.querySelector("input[type=checkbox][name=share]");
            if (share) {
                data.set("share", String(share.checked));
            }
            let found = knownBarcode;
            if (!found && barcodeField) {
                for (const shot of shots) {
                    found = await barcodeIn(shot.blob);
                    if (found) {
                        break;
                    }
                }
            }
            data.set("barcode", found || "");
            if (pending !== controller) {
                return;
            }

            const response = await fetch("/food/label", {
                method: "POST",
                body: data,
                headers: { "RequestVerificationToken": window.gymToken(), "HX-Request": "true" },
                signal: controller.signal,
            });
            const text = await response.text();
            if (pending !== controller) {
                return;
            }
            if (!response.ok) {
                toast(response.status === 429
                    ? t("Too many photos for now. Try again in a few minutes.")
                    : response.status === 400 ? text : t("Couldn't read the label. Enter the values by hand."));
                return;
            }

            const fresh = new DOMParser().parseFromString(text, "text/html").querySelector("[data-label-form]");
            if (!fresh) {
                toast(t("Couldn't read the label. Enter the values by hand."));
                return;
            }
            const placed = document.importNode(fresh, true);
            if (slot) {
                slot.replaceChildren(placed);
            } else {
                host.replaceWith(placed);
            }
            // A read from the slots closes them: on the manual form they went with the old form; on the Scan tab they stay above.
            const scope = trigger.closest("[data-label-photos]");
            if (scope && trigger.matches("[data-label-read]")) {
                setMore(scope, false);
            }
            // A barcode the detector found in the photo may already be a food (SPEC 7); scanner.js offers it.
            if (!knownBarcode && placed.querySelector("[data-barcode-input]")?.value) {
                placed.dispatchEvent(new CustomEvent("label:barcode", { bubbles: true }));
            }
            setOnline();
            placed.querySelector(".banner")?.scrollIntoView({ block: "start", behavior: "smooth" });
        } catch (error) {
            if (error.name !== "AbortError" && pending === controller) {
                toast(t("Couldn't read the label. Enter the values by hand."));
            }
        } finally {
            // A newer photo owns the indicator now; only the latest request clears it.
            if (pending === controller) {
                busy.classList.remove("is-busy");
                pending = null;
                pendingBusy = null;
                drawPanels();
            }
        }
    }

    const onChange = (event) => {
        const input = event.target;
        if (input.matches("[data-label-slot-photo]")) {
            pickInto(input);
        } else if (input.matches("[data-label-photo]")) {
            const file = input.files && input.files[0];
            input.value = "";
            if (file) {
                read([file], input);
            }
        }
    };
    const onClick = (event) => {
        const button = event.target.closest("[data-label-more], [data-label-less], [data-label-remove], [data-label-read]");
        if (!button || button.disabled) {
            return;
        }
        const scope = button.closest("[data-label-photos]");
        if (button.matches("[data-label-more]")) {
            setMore(scope, true);
        } else if (button.matches("[data-label-less]")) {
            setMore(scope, false);
        } else if (button.matches("[data-label-remove]")) {
            const panel = button.closest("[data-label-panel]");
            const list = pickedIn(panel);
            const index = Number(button.closest("[data-label-slot-index]").dataset.labelSlotIndex);
            if (list[index]) {
                URL.revokeObjectURL(list[index].url);
                list[index] = null;
            }
            drawPanel(panel);
        } else {
            const shots = pickedIn(button.closest("[data-label-panel]")).filter(Boolean);
            if (shots.length) {
                read(shots, button);
            }
        }
    };
    const onLeave = () => pending?.abort();

    root.addEventListener("change", onChange);
    root.addEventListener("click", onClick);
    window.addEventListener("online", setOnline);
    window.addEventListener("offline", setOnline);
    window.addEventListener("pagehide", onLeave);
    setOnline();
    return () => {
        onLeave();
        root.removeEventListener("change", onChange);
        root.removeEventListener("click", onClick);
        root.querySelectorAll("[data-label-panel]").forEach(forget);
        window.removeEventListener("online", setOnline);
        window.removeEventListener("offline", setOnline);
        window.removeEventListener("pagehide", onLeave);
    };
});
