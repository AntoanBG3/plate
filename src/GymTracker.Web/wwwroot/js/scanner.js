// Barcode scanning for the Food page (SPEC 7). Safari ships BarcodeDetector disabled, so this uses the
// barcode-detector ponyfill (ZXing-C++ in WebAssembly), served from this app rather than a CDN.
// The camera opens without changing the URL, since a route change can stop the stream or re-prompt on iOS.
(function () {
    "use strict";

    const root = document.getElementById("scanner");
    const api = window.BarcodeDetectionAPI;
    if (!root || !api) {
        return;
    }

    api.prepareZXingModule({
        overrides: {
            locateFile: (path, prefix) => path.endsWith(".wasm") ? "/lib/barcode-detector/zxing_reader.wasm" : prefix + path,
        },
    });

    const detector = new api.BarcodeDetector({ formats: ["ean_13", "ean_8", "upc_a", "upc_e"] });
    const video = root.querySelector("video");
    const status = root.querySelector("[data-status]");
    const form = document.getElementById("barcode-form");
    const codeInput = form.querySelector('input[name="code"]');
    let stream = null;
    let timer = null;

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
        say(`Found ${code}. Looking it up…`);
        codeInput.value = code;
        form.requestSubmit();
    }

    root.querySelector("[data-start]").addEventListener("click", async () => {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            say("This browser can't open the camera. Type the digits or take a photo instead.");
            return;
        }
        try {
            stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } }, audio: false });
        } catch {
            say("Camera access was refused. Type the digits or take a photo instead.");
            return;
        }

        video.srcObject = stream;
        video.hidden = false;
        await video.play().catch(() => {});
        say("Point the camera at the barcode.");

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
    });

    root.querySelector("[data-photo]").addEventListener("change", async (event) => {
        const file = event.target.files && event.target.files[0];
        if (!file) {
            return;
        }
        stop();
        say("Reading the photo…");
        try {
            const codes = await detector.detect(await createImageBitmap(file));
            if (codes.length) {
                found(codes[0].rawValue);
            } else {
                say("No barcode found in that photo. Try again closer, or type the digits.");
            }
        } catch {
            say("Couldn't read that photo. Type the digits instead.");
        }
    });

    window.addEventListener("pagehide", stop);
})();
