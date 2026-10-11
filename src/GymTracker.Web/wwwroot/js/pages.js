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

    // Runs fn(el, index) once el is on screen: at once if any of it shows already (or it has no box to wait for),
    // otherwise when its top comes within 85 % of the viewport height, as apple.com's staggered reveals do. index is
    // the element's place among those coming into view together, for staggering. Waits end when the page is swapped.
    let seenObserver = null;
    const waiting = new Map();
    window.gymWhenSeen = (el, fn) => {
        const box = el.getBoundingClientRect();
        if (!("IntersectionObserver" in window) || (box.width === 0 && box.height === 0) || (box.top < window.innerHeight && box.bottom > 0)) {
            fn(el, 0);
            return;
        }
        if (!seenObserver) {
            seenObserver = new IntersectionObserver((entries) => {
                let index = 0;
                entries.forEach((entry) => {
                    if (!entry.isIntersecting) {
                        return;
                    }
                    seenObserver.unobserve(entry.target);
                    const callbacks = waiting.get(entry.target) || [];
                    waiting.delete(entry.target);
                    callbacks.forEach((callback) => callback(entry.target, index));
                    index++;
                });
            }, { rootMargin: "0px 0px -15% 0px" });
        }
        if (!waiting.has(el)) {
            waiting.set(el, []);
            seenObserver.observe(el);
        }
        waiting.get(el).push(fn);
    };
    document.addEventListener("gym:beforeswap", () => {
        if (seenObserver) {
            seenObserver.disconnect();
        }
        waiting.clear();
    });
})();
