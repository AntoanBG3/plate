// The setup chat (SPEC 6.4): shows the user's message at once while the server asks the model, keeps the newest
// message in view, sends on Enter, and passes the phone's time zone with the summary.
window.gymModule("setup", (root) => {
    "use strict";

    const chat = root.querySelector("[data-chat]");
    const last = chat && chat.lastElementChild;
    if (last) {
        requestAnimationFrame(() => last.scrollIntoView({ block: "center" }));
    }

    function echo(text) {
        if (!chat || !text.trim()) {
            return;
        }
        const bubble = document.createElement("p");
        bubble.className = "bubble";
        bubble.textContent = text.trim();
        chat.appendChild(bubble);
        bubble.scrollIntoView({ block: "center", behavior: "smooth" });
    }

    const onSubmit = (event) => {
        const form = event.target;
        if (form.matches("[data-composer]")) {
            echo(form.querySelector("textarea").value);
        } else if (form.matches("[data-quick-reply]")) {
            echo(form.querySelector("input[name=text]").value);
            root.querySelectorAll("[data-quick-reply] button").forEach((b) => { b.disabled = true; });
        }
    };
    root.addEventListener("submit", onSubmit);

    const textarea = root.querySelector("[data-composer] textarea");
    const onKey = (event) => {
        if (event.key === "Enter" && !event.shiftKey && !event.isComposing && textarea.value.trim()) {
            event.preventDefault();
            textarea.form.requestSubmit();
        }
    };
    if (textarea) {
        textarea.addEventListener("keydown", onKey);
    }

    try {
        const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
        root.querySelectorAll("[data-time-zone]").forEach((input) => { input.value = zone || ""; });
    } catch (e) {
        // No Intl time zone: the profile keeps its own.
    }

    return () => {
        root.removeEventListener("submit", onSubmit);
        if (textarea) {
            textarea.removeEventListener("keydown", onKey);
        }
    };
});
