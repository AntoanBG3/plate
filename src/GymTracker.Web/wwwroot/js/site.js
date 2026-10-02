// Shows htmx failures as a toast instead of failing silently.
(function () {
    const toast = document.getElementById("toast");
    let timer;

    function show(text) {
        toast.textContent = text;
        toast.hidden = false;
        clearTimeout(timer);
        timer = setTimeout(() => { toast.hidden = true; }, 5000);
    }

    document.body.addEventListener("htmx:responseError", (event) => {
        const xhr = event.detail.xhr;
        if (xhr.status === 401 || xhr.responseURL.includes("/account/login")) {
            window.location.href = "/account/login?returnUrl=" + encodeURIComponent(window.location.pathname);
            return;
        }
        show(xhr.status === 400 && xhr.responseText ? xhr.responseText : "Something went wrong (" + xhr.status + "). Try again.");
    });

    document.body.addEventListener("htmx:sendError", () => {
        show("No connection. The set was not saved; tap ✓ again when you have signal.");
    });
})();
