// The offline page is served by the service worker: take language and theme from the cookies the app sets,
// and make "Try again" reload.
(function () {
    var c = document.cookie;
    var theme = (c.match(/(?:^|; )gym\.theme=(\w+)/) || [])[1];
    if (theme) {
        document.documentElement.setAttribute("data-theme", theme);
    }
    if ((c.match(/(?:^|; )gym\.lang=(\w+)/) || [])[1] === "bg") {
        document.documentElement.lang = "bg";
        document.title = "Няма връзка · Ряпа";
    }
    document.addEventListener("click", function (event) {
        if (event.target.closest("[data-reload]")) {
            location.reload();
        }
    });
})();
