// Per-page setup that survives soft navigation. A page marks an element with data-module="name"; the module's
// init runs once per element and may return a cleanup that runs before the content is swapped out.
// Loaded without defer, so page scripts at the end of <body> can register before the deferred scripts run.
(function () {
    "use strict";

    const modules = new Map();
    let cleanups = [];

    function mount(scope) {
        scope.querySelectorAll("[data-module]").forEach((el) => {
            const init = modules.get(el.dataset.module);
            if (!init || el.gymMounted) {
                return;
            }
            el.gymMounted = true;
            const cleanup = init(el);
            if (typeof cleanup === "function") {
                cleanups.push(cleanup);
            }
        });
    }

    window.gymModule = (name, init) => {
        modules.set(name, init);
        mount(document);
    };

    document.addEventListener("gym:beforeswap", () => {
        cleanups.forEach((c) => {
            try {
                c();
            } catch (e) {
                console.error(e);
            }
        });
        cleanups = [];
    });
    document.addEventListener("gym:page", () => mount(document));
    document.addEventListener("DOMContentLoaded", () => mount(document));
})();
