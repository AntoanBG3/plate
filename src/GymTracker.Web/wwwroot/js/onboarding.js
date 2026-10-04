// The setup chat (SPEC 6.4): shows the user's message at once while the server asks the model, keeps the newest
// message in view, sends on Enter, and passes the phone's time zone with the summary.
window.gymModule("setup", (root) => {
    "use strict";

    const chat = root.querySelector("[data-chat]");
    const last = chat && chat.lastElementChild;
    if (last) {
        requestAnimationFrame(() => last.scrollIntoView({ block: "center" }));
    }

    // Shows the user's message and a typing bubble at once; the server's page replaces both when the reply is in.
    function echo(text) {
        if (!chat || !text.trim()) {
            return;
        }
        const mine = document.createElement("p");
        mine.className = "msg from-me";
        mine.textContent = text.trim();
        const typing = document.createElement("p");
        typing.className = "msg from-app typing";
        typing.setAttribute("aria-hidden", "true");
        typing.innerHTML = "<i></i><i></i><i></i>";
        chat.append(mine, typing);
        const chips = root.querySelector(".reply-chips");
        if (chips) {
            chips.hidden = true;
        }
        typing.scrollIntoView({ block: "center", behavior: "smooth" });
    }

    const onSubmit = (event) => {
        const form = event.target;
        if (form.matches("[data-composer]")) {
            const textarea = form.querySelector("textarea");
            echo(textarea.value);
            // After the navigation handler has read the form.
            setTimeout(() => { textarea.value = ""; });
        } else if (form.matches("[data-quick-reply]")) {
            echo(form.querySelector("input[name=text]").value);
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
