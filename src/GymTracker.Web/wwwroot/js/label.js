// Label photos for the manual food form (SPEC 6.6): shrink the photo on the phone, look for a barcode in it, send it
// to /food/label and swap in the form the server filled. Only the latest photo's answer is used; nothing is saved
// until the form is.
window.gymModule("label", (root) => {
    "use strict";

    let pending = null;
    let detector = null;

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
    }

    async function read(input) {
        const file = input.files && input.files[0];
        input.value = "";
        if (!file) {
            return;
        }

        // From the Scan tab the filled form opens in the slot; otherwise it replaces the form the button sits in.
        const slot = input.matches("[data-label-start]") ? root.querySelector("[data-label-slot]") : null;
        const host = slot ? null : input.closest("[data-label-form]");
        const form = host && host.querySelector("form");
        const busy = slot ? root : host.querySelector("[data-label]");

        pending?.abort();
        const controller = new AbortController();
        pending = controller;
        busy.classList.add("is-busy");
        try {
            let shot;
            try {
                shot = await window.gymImage.shrink(file, { maxSide: 2000, quality: 0.85, maxBytes: 2 * 1024 * 1024, steps: [[2000, 0.7], [1600, 0.7], [1200, 0.6]] });
            } catch {
                if (pending === controller) {
                    toast(t("Couldn't read that picture. Try another photo."));
                }
                return;
            }

            const data = new FormData();
            data.append("photo", shot.blob, "label.jpg");
            data.set("width", String(shot.width));
            data.set("height", String(shot.height));
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
            data.set("barcode", knownBarcode || (barcodeField ? await barcodeIn(shot.blob) || "" : ""));
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
            }
        }
    }

    const onChange = (event) => {
        if (event.target.matches("[data-label-photo]")) {
            read(event.target);
        }
    };
    const onLeave = () => pending?.abort();

    root.addEventListener("change", onChange);
    window.addEventListener("online", setOnline);
    window.addEventListener("offline", setOnline);
    window.addEventListener("pagehide", onLeave);
    setOnline();
    return () => {
        onLeave();
        root.removeEventListener("change", onChange);
        window.removeEventListener("online", setOnline);
        window.removeEventListener("offline", setOnline);
        window.removeEventListener("pagehide", onLeave);
    };
});
