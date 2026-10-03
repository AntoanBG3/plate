// t(text, ...args): the translation from the layout's #i18n block, or the English text.
// Arguments fill {0}, {1}. Loaded before the other scripts.
(function () {
    "use strict";
    let table = {};
    try {
        table = JSON.parse(document.getElementById("i18n").textContent || "{}");
    } catch {
        table = {};
    }
    window.t = function (key, ...args) {
        const text = table[key] || key;
        return text.replace(/\{(\d+)\}/g, (m, i) => (args[i] !== undefined ? String(args[i]) : m));
    };
})();
