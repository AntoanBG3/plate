const assert = require("node:assert/strict");
const { test } = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function loader() {
    const source = fs.readFileSync(path.resolve(__dirname, "../../src/Plate.Web/wwwroot/js/nav.js"), "utf8");
    const start = source.indexOf("    function loadScript(src) {");
    const end = source.indexOf("\n    function markActiveTab", start);
    assert.ok(start >= 0 && end > start);
    const scripts = [];
    const context = {
        URL, location: { href: "https://localhost/food" }, loaded: new Set(),
        document: {
            createElement: () => ({ remove() { this.removed = true; } }),
            head: { appendChild: (script) => scripts.push(script) },
        },
    };
    vm.createContext(context);
    vm.runInContext(source.slice(start, end) + "\nthis.load = loadScript;", context);
    return { load: context.load, scripts };
}

test("a failed page script is retried and a successful retry is reused", async () => {
    const { load, scripts } = loader();
    const first = load("/js/progress.js?v=one");
    const rejected = assert.rejects(first, /offline/);
    scripts[0].onerror(new Error("offline"));
    await rejected;
    const retry = load("/js/progress.js?v=one");
    assert.equal(scripts.length, 2, "failure must not leave a script marked as loaded");
    scripts[1].onload();
    await retry;
    await load("/js/progress.js?v=one");
    assert.equal(scripts.length, 2, "successful scripts still load once");
});
