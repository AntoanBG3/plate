// Passkey (Face ID / Touch ID) sign-in and registration. The server side is PasskeyController. Sign-in is
// usernameless: the login page also offers saved passkeys in the keyboard's autofill bar (conditional UI).
(function () {
    "use strict";

    if (!window.PublicKeyCredential || !navigator.credentials) {
        return;
    }

    // base64url <-> ArrayBuffer, for browsers without PublicKeyCredential.parse*FromJSON and toJSON().
    function decode(s) {
        const b64 = s.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (s.length % 4)) % 4);
        return Uint8Array.from(atob(b64), (c) => c.charCodeAt(0)).buffer;
    }

    function encode(buffer) {
        let bin = "";
        new Uint8Array(buffer).forEach((b) => { bin += String.fromCharCode(b); });
        return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    const withIds = (list) => (list || []).map((c) => Object.assign({}, c, { id: decode(c.id) }));

    function creationOptions(json) {
        if (PublicKeyCredential.parseCreationOptionsFromJSON) {
            return PublicKeyCredential.parseCreationOptionsFromJSON(json);
        }
        return Object.assign({}, json, {
            challenge: decode(json.challenge),
            user: Object.assign({}, json.user, { id: decode(json.user.id) }),
            excludeCredentials: withIds(json.excludeCredentials),
        });
    }

    function requestOptions(json) {
        if (PublicKeyCredential.parseRequestOptionsFromJSON) {
            return PublicKeyCredential.parseRequestOptionsFromJSON(json);
        }
        return Object.assign({}, json, { challenge: decode(json.challenge), allowCredentials: withIds(json.allowCredentials) });
    }

    function credentialJson(credential) {
        if (credential.toJSON) {
            return JSON.stringify(credential.toJSON());
        }
        const r = credential.response;
        const response = { clientDataJSON: encode(r.clientDataJSON) };
        if (r.attestationObject) {
            response.attestationObject = encode(r.attestationObject);
            response.transports = r.getTransports ? r.getTransports() : [];
        }
        if (r.authenticatorData) {
            response.authenticatorData = encode(r.authenticatorData);
            response.signature = encode(r.signature);
            response.userHandle = r.userHandle ? encode(r.userHandle) : null;
        }
        return JSON.stringify({
            id: credential.id,
            rawId: encode(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment || null,
            clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {},
            response,
        });
    }

    async function post(url, body, signal) {
        const headers = { RequestVerificationToken: window.gymToken() };
        if (body) {
            headers["Content-Type"] = "application/json";
        }
        const response = await fetch(url, { method: "POST", credentials: "same-origin", headers, body: body ? JSON.stringify(body) : undefined, signal });
        if (!response.ok) {
            throw new Error(String(response.status));
        }
        return response;
    }

    const cancelled = (e) => e && (e.name === "AbortError" || e.name === "NotAllowedError");

    async function register(button) {
        button.disabled = true;
        try {
            const options = await (await post("/account/passkey/creation-options")).json();
            const credential = await navigator.credentials.create({ publicKey: creationOptions(options) });
            await post("/account/passkey/register", { credential: credentialJson(credential) });
            if (window.gymNav) {
                window.gymNav("/settings", { push: false });
            } else {
                location.reload();
            }
        } catch (e) {
            if (!cancelled(e)) {
                window.gymToast(t("Couldn't add the passkey. Try again."));
            }
        } finally {
            button.disabled = false;
        }
    }

    let pending = null;

    async function signIn(conditional) {
        if (pending) {
            pending.abort();
        }
        const abort = new AbortController();
        pending = abort;
        try {
            const options = await (await post("/account/passkey/request-options", null, abort.signal)).json();
            const credential = await navigator.credentials.get({
                publicKey: requestOptions(options),
                mediation: conditional ? "conditional" : "optional",
                signal: abort.signal,
            });
            const returnUrl = new URLSearchParams(location.search).get("returnUrl");
            const result = await (await post("/account/passkey/login", { credential: credentialJson(credential), returnUrl })).json();
            location.href = result.redirect;
        } catch (e) {
            if (!abort.signal.aborted && !cancelled(e)) {
                window.gymToast(t("That passkey didn't work. Try again or use your password."));
            }
        }
    }

    // The admin area's unlock (SPEC 12): one of this account's own passkeys, with Face ID or Touch ID.
    async function unlock(button) {
        button.disabled = true;
        try {
            const options = await (await post("/admin/unlock/options")).json();
            const credential = await navigator.credentials.get({ publicKey: requestOptions(options) });
            const returnUrl = new URLSearchParams(location.search).get("returnUrl");
            const result = await (await post("/admin/unlock", { credential: credentialJson(credential), returnUrl })).json();
            location.href = result.redirect;
        } catch (e) {
            if (!cancelled(e)) {
                window.gymToast(t("That passkey didn't work. Try again."));
            }
            button.disabled = false;
        }
    }

    window.gymModule("passkey-unlock", (button) => {
        button.hidden = false;
        button.addEventListener("click", () => unlock(button));
    });

    window.gymModule("passkey-add", (button) => {
        button.hidden = false;
        button.addEventListener("click", () => register(button));
    });

    window.gymModule("passkey-login", (button) => {
        // Signing in with the password ends the waiting passkey request, or keeps it from starting (the availability
        // check is asynchronous): its options request would otherwise reach the server after the sign-in, be refused
        // (the anti-forgery token is the signed-out user's) and say the passkey didn't work.
        let stopped = false;
        const form = button.closest("main") && button.closest("main").querySelector("form");
        const stop = () => {
            stopped = true;
            if (pending) {
                pending.abort();
            }
        };
        button.hidden = false;
        button.addEventListener("click", () => signIn(false));
        if (PublicKeyCredential.isConditionalMediationAvailable) {
            PublicKeyCredential.isConditionalMediationAvailable().then((available) => {
                if (available && !stopped) {
                    signIn(true);
                }
            }).catch(() => {});
        }
        if (form) {
            form.addEventListener("submit", stop);
        }
        return () => {
            stop();
            if (form) {
                form.removeEventListener("submit", stop);
            }
        };
    });
})();
