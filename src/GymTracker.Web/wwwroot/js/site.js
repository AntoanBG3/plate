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
        show("No connection. That change was not saved; try again when you have signal.");
    });
})();

if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/sw.js").catch(() => {});
}

// Live per-row totals on the food confirmation list and portion forms.
document.addEventListener("input", (event) => {
    const row = event.target.closest("[data-per100]");
    if (!row) {
        return;
    }
    const grams = parseFloat((row.querySelector('input[name$="Grams"], input[name="grams"]') || {}).value) || 0;
    const per = row.dataset.per100.split(",").map(Number);
    const out = row.querySelector("[data-totals]");
    if (out) {
        const f = grams / 100;
        out.textContent = `${Math.round(per[0] * f)} kcal · ${(per[1] * f).toFixed(1)} g protein`;
    }
});
