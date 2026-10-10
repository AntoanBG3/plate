// Barcode scanning on the Food add page (SPEC 7). Safari ships BarcodeDetector disabled, so this uses the
// barcode-detector ponyfill (ZXing-C++ in WebAssembly), served from this app rather than a CDN.
// The camera opens in a full-screen sheet without changing the URL, since a route change can stop the
// stream or re-prompt on iOS. Closing the sheet stops the camera. "Scan a barcode" looks the code up; the scan
// button beside a barcode field puts the code in that field instead. This module also runs the barcode field itself:
// the share row shows only once there is a barcode (SPEC 4).
window.gymModule("scanner", (root) => {
    "use strict";

    // Others find a shared food by its barcode, so the share choice shows only once there is one (SPEC 4).
    function toggleShare(form) {
        const input = form && form.querySelector("[data-barcode-input]");
        if (!input) {
            return;
        }
        const show = input.value.replace(/\D/g, "").length >= 8;
        form.querySelectorAll("[data-share]").forEach((el) => { el.hidden = !show; });
    }

    const onInput = (event) => {
        if (event.target.matches("[data-barcode-input]")) {
            toggleShare(event.target.closest("form"));
        }
    };
    document.addEventListener("input", onInput);

    const api = window.BarcodeDetectionAPI;
    if (!api) {
        return () => document.removeEventListener("input", onInput);
    }

    api.prepareZXingModule({
        overrides: {
            locateFile: (path, prefix) => path.endsWith(".wasm") ? "/lib/barcode-detector/zxing_reader.wasm" : prefix + path,
        },
    });

    const detector = new api.BarcodeDetector({ formats: ["ean_13", "ean_8", "upc_a", "upc_e"] });
    // After a soft navigation (nav.js) the sheet's markup was parsed by DOMParser, and iOS keeps a <video> made
    // there black even while the camera runs. A video created in this document plays, however the page loaded.
    const video = document.createElement("video");
    video.muted = true;
    video.playsInline = true;
    video.hidden = true;
    root.querySelector("video").replaceWith(video);
    const status = root.querySelector("[data-status]");
    let stream = null;
    let timer = null;
    // The field a found code goes into; null means look it up (the Scan tab's main button).
    let target = null;

    function say(text) {
        status.textContent = text;
    }

    function stop() {
        clearInterval(timer);
        timer = null;
        if (stream) {
            stream.getTracks().forEach((t) => t.stop());
            stream = null;
        }
        video.hidden = true;
    }

    function found(code) {
        stop();
        const field = target;
        if (field) {
            target = null;
            field.value = code;
            field.dispatchEvent(new Event("input", { bubbles: true }));
            field.dispatchEvent(new Event("change", { bubbles: true }));
            window.gymSheet.hide();
            return;
        }
        const form = document.getElementById("barcode-form");
        say(t("Found {0}. Looking it up…", code));
        form.querySelector('input[name="code"]').value = code;
        form.requestSubmit();
    }

    async function open(field) {
        target = field;
        window.gymSheet.show(root);
        say(t("Point the camera at the barcode."));
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            say(t("This browser can't open the camera. Type the digits or take a photo instead."));
            return;
        }
        try {
            stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } }, audio: false });
        } catch {
            say(t("Camera access was refused. Type the digits or take a photo instead."));
            return;
        }

        video.srcObject = stream;
        video.hidden = false;
        await video.play().catch(() => {});

        let busy = false;
        timer = setInterval(async () => {
            if (busy || video.readyState < 2) {
                return;
            }
            busy = true;
            try {
                const codes = await detector.detect(video);
                if (codes.length) {
                    found(codes[0].rawValue);
                }
            } catch {
                // Frame not ready yet; try the next one.
            } finally {
                busy = false;
            }
        }, 250);
    }

    // Delegated: the manual form is replaced after a label photo (label.js), and its scan button with it.
    const onClick = (event) => {
        const button = event.target.closest("[data-start], [data-scan-into]");
        if (!button) {
            return;
        }
        open(button.matches("[data-scan-into]") ? button.closest(".field-row").querySelector("[data-barcode-input]") : null);
    };
    document.addEventListener("click", onClick);

    root.addEventListener("sheet:close", () => {
        stop();
        target = null;
    });

    root.querySelector("[data-photo]").addEventListener("change", async (event) => {
        const file = event.target.files && event.target.files[0];
        event.target.value = "";
        if (!file) {
            return;
        }
        stop();
        say(t("Reading the photo…"));
        try {
            const codes = await detector.detect(await createImageBitmap(file));
            if (codes.length) {
                found(codes[0].rawValue);
            } else {
                say(t("No barcode found in that photo. Try again closer, or type the digits."));
            }
        } catch {
            say(t("Couldn't read that photo. Type the digits instead."));
        }
    });

    window.addEventListener("pagehide", stop);
    return () => {
        stop();
        document.removeEventListener("input", onInput);
        document.removeEventListener("click", onClick);
        window.removeEventListener("pagehide", stop);
    };
});
