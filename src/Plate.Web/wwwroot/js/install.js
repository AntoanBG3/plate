// "Add to Home Screen": the Today card and the guide at /settings/install. The server renders every variant hidden;
// this script picks the one for this browser. iOS has no install API, so it gets steps; Chrome on Android fires
// beforeinstallprompt, which is kept here so an Install button can open the browser's own prompt.
(function () {
    "use strict";

    let deferredPrompt = null;

    window.addEventListener("beforeinstallprompt", (event) => {
        event.preventDefault(); // the card or the guide offers it instead of the browser's mini-infobar
        deferredPrompt = event;
        document.dispatchEvent(new CustomEvent("gym:installable"));
    });

    window.addEventListener("appinstalled", () => {
        deferredPrompt = null;
        document.dispatchEvent(new CustomEvent("gym:installed"));
    });

    function standalone() {
        return window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
    }

    // standalone, ios-safari, ios-other, in-app, android or other.
    function platform() {
        if (standalone()) {
            return "standalone";
        }
        const ua = navigator.userAgent;
        // Facebook, Messenger, Instagram, LINE, WeChat, Snapchat, TikTok and X open links in a browser of their own
        // that can't add to the Home Screen.
        if (/FBAN|FBAV|FB_IAB|Instagram|Line\/|MicroMessenger|Snapchat|BytedanceWebview|musical_ly|TwitterAndroid|Twitter for iPhone/.test(ua)) {
            return "in-app";
        }
        const ios = /iPhone|iPad|iPod/.test(ua) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
        if (ios) {
            return /CriOS|FxiOS|EdgiOS|OPiOS|YaBrowser|DuckDuckGo/.test(ua) ? "ios-other" : "ios-safari";
        }
        if (/Android/.test(ua)) {
            return "android";
        }
        return "other";
    }

    const dismissKey = () => "gym.installHint.dismissed." + encodeURIComponent(document.documentElement.dataset.userId || "");

    function dismissed() {
        try {
            return localStorage.getItem(dismissKey()) === "1";
        } catch {
            return false;
        }
    }

    // Shows the elements marked for this platform (data-platform="ios-safari android", say) and hides the rest.
    function choose(root, current) {
        root.querySelectorAll("[data-platform]").forEach((el) => {
            el.hidden = !el.dataset.platform.split(" ").includes(current);
        });
    }

    // Install buttons (data-install-prompt) show only while the browser has a prompt to open, and what they replace
    // (data-install-fallback) only while it hasn't.
    function wirePrompt(root, onInstalled) {
        const buttons = root.querySelectorAll("[data-install-prompt]");
        const fallbacks = root.querySelectorAll("[data-install-fallback]");
        const sync = () => {
            buttons.forEach((b) => (b.hidden = deferredPrompt === null));
            fallbacks.forEach((f) => (f.hidden = deferredPrompt !== null));
        };
        const click = async () => {
            if (!deferredPrompt) {
                return;
            }
            const prompt = deferredPrompt;
            deferredPrompt = null;
            sync();
            await prompt.prompt();
            const choice = await prompt.userChoice;
            if (choice.outcome === "accepted") {
                onInstalled();
            }
        };
        buttons.forEach((b) => b.addEventListener("click", click));
        document.addEventListener("gym:installable", sync);
        document.addEventListener("gym:installed", onInstalled);
        sync();
        return () => {
            buttons.forEach((b) => b.removeEventListener("click", click));
            document.removeEventListener("gym:installable", sync);
            document.removeEventListener("gym:installed", onInstalled);
        };
    }

    // The Today card: phones only, never in the installed app, and gone for good once dismissed on this device.
    window.gymModule("install-card", (card) => {
        const current = platform();
        if (current === "standalone" || current === "other" || dismissed()) {
            card.hidden = true;
            return;
        }
        card.hidden = false;
        const hide = () => {
            card.hidden = true;
        };
        const dismiss = () => {
            try {
                localStorage.setItem(dismissKey(), "1");
            } catch {
                // No storage: it comes back on the next visit, and Settings still has the guide.
            }
            hide();
        };
        const button = card.querySelector("[data-install-dismiss]");
        button.addEventListener("click", dismiss);
        const unwire = wirePrompt(card, hide);
        return () => {
            button.removeEventListener("click", dismiss);
            unwire();
        };
    });

    window.gymModule("install-guide", (root) => {
        choose(root, platform());
        return wirePrompt(root, () => choose(root, "standalone-now"));
    });
})();
