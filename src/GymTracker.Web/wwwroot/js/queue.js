// Account-scoped pending changes. If browser storage fails, keep sending from memory and
// tell the user to keep the page open. An older, ownerless (v1) queue is adopted only where the
// server can refuse a wrong owner (sets), or when the user chooses to (feedback).
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

        // ---- the ownerless v1 queue left by the previous version ----

        const legacyKey = `${name}.v1`;

        function readLegacy() {
            try {
                const value = JSON.parse(localStorage.getItem(legacyKey) || "[]");
                return Array.isArray(value) ? value : [];
            } catch {
                return [];
            }
        }

        function writeLegacy(items) {
            try {
                if (items.length) {
                    localStorage.setItem(legacyKey, JSON.stringify(items));
                } else {
                    localStorage.removeItem(legacyKey);
                }
                return true;
            } catch {
                return false;
            }
        }

        function hasLegacy() {
            return !!owner && readLegacy().length > 0;
        }

        // Moves the legacy items this account has not tried yet into its own queue, marked `legacy`, and returns how
        // many. They leave the v1 queue only once the account's queue is saved, so a storage failure never loses them;
        // a repeated ClientId is success on the server, so sending one twice is harmless. `keep` drops expired items.
        function adoptLegacy(id, keep = () => true) {
            if (!owner) {
                return 0;
            }
            const legacy = readLegacy().filter(keep);
            const take = legacy.filter((x) => !(x.triedBy || []).includes(owner));
            const rest = legacy.filter((x) => !take.includes(x));
            if (take.length) {
                const own = load();
                const ids = new Set(own.map(id));
                const added = take.filter((x) => !ids.has(id(x))).map((x) => ({ ...x, legacy: true }));
                if (!save(own.concat(added))) {
                    return take.length;
                }
            }
            writeLegacy(rest);
            return take.length;
        }

        // A legacy item this account could not send (its session or parse belongs to someone else) goes back to the
        // v1 queue for the account that made it, and is not offered to this account again.
        function returnLegacy(item, id) {
            const { legacy, ...original } = item;
            const others = readLegacy().filter((x) => id(x) !== id(item));
            if (!writeLegacy(others.concat({ ...original, triedBy: [...(item.triedBy || []), owner] }))) {
                return false;
            }
            save(load().filter((x) => id(x) !== id(item)));
            return true;
        }

        return {
            load, save, hasLegacy, adoptLegacy, returnLegacy,
            legacyItems: () => (owner ? readLegacy() : []),
            discardLegacy: () => writeLegacy([]),
            get durable() { return durable; },
            active: () => !paused && !!owner && document.documentElement.dataset.userId === owner,
            pause: () => { paused = true; },
            headers: () => ({ "X-Gym-Queue": "1", "X-Gym-User": owner }),
        };
    };
})();
