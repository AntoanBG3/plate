// Account-scoped pending changes. If browser storage fails, keep sending from memory and
// tell the user to keep the page open. An older, ownerless queue is never adopted on sign-in.
(function () {
    "use strict";

    window.gymQueue = function (name) {
        const owner = document.documentElement.dataset.userId || "";
        const key = `${name}.v2.${encodeURIComponent(owner)}`;
        let memory = null;
        let durable = true;
        let paused = false;

        function load() {
            if (!owner) {
                return [];
            }
            if (memory !== null) {
                return memory.slice();
            }
            try {
                const value = JSON.parse(localStorage.getItem(key) || "[]");
                return Array.isArray(value) ? value : [];
            } catch {
                durable = false;
                return [];
            }
        }

        function save(items) {
            memory = items.slice();
            if (!owner) {
                return false;
            }
            try {
                if (items.length) {
                    localStorage.setItem(key, JSON.stringify(items));
                } else {
                    localStorage.removeItem(key);
                }
                memory = null;
                durable = true;
                return true;
            } catch {
                durable = false;
                return false;
            }
        }

        function hasLegacy() {
            try {
                return owner && JSON.parse(localStorage.getItem(`${name}.v1`) || "[]").length > 0;
            } catch {
                return false;
            }
        }

        return {
            load, save, hasLegacy,
            get durable() { return durable; },
            active: () => !paused && !!owner && document.documentElement.dataset.userId === owner,
            pause: () => { paused = true; },
            headers: () => ({ "X-Gym-Queue": "1", "X-Gym-User": owner }),
        };
    };
})();
