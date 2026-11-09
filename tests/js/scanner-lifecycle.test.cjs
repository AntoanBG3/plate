const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../../src/Plate.Web/wwwroot/js/scanner.js"), "utf8");
const settle = () => new Promise(setImmediate);

function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}

function camera() {
    const trackStops = [0, 0];
    const tracks = trackStops.map((_, i) => ({ stop() { trackStops[i]++; } }));
    return { trackStops, getTracks: () => tracks };
}

function scanner({ play = async () => {}, detect = async () => [] } = {}) {
    const clicks = [], requests = [], submissions = [];
    const sheetEvents = {}, windowEvents = {};
    const intervals = new Map();
    let factory, lastInterval = 0, plays = 0;
    const video = { srcObject: null, readyState: 2, play() { plays++; return play(); } };
    const status = { textContent: "" };
    const sheet = {
        querySelector(selector) {
            if (selector === "video") return { replaceWith() {} };
            if (selector === "[data-status]") return status;
            return { addEventListener() {} };
        },
        addEventListener(name, callback) { sheetEvents[name] = callback; },
    };
    const code = { value: "" };
    const form = { querySelector: () => code, requestSubmit() { submissions.push(code.value); } };
    vm.runInNewContext(source, {
        document: {
            addEventListener(name, callback) { if (name === "click") clicks.push(callback); },
            removeEventListener() {}, createElement: () => video, getElementById: () => form,
        },
        navigator: { mediaDevices: { getUserMedia() {
            const request = deferred();
            requests.push(request);
            return request.promise;
        } } },
        t: text => text,
        setInterval(callback) { intervals.set(++lastInterval, callback); return lastInterval; },
        clearInterval(id) { intervals.delete(id); },
        window: {
            gymModule(_name, setup) { factory = setup; },
            BarcodeDetectionAPI: { prepareZXingModule() {}, BarcodeDetector: class { detect = detect; } },
            gymSheet: { show() {} },
            addEventListener(name, callback) { windowEvents[name] = callback; },
            removeEventListener() {},
        },
    });
    const cleanup = factory(sheet);
    return {
        open() {
            const button = { matches: () => false };
            clicks.forEach(callback => callback({ target: { closest: selector => selector.includes("data-start") ? button : null } }));
        },
        close: sheetEvents["sheet:close"], cleanup, pagehide: windowEvents.pagehide,
        requests, intervals, submissions, video, get plays() { return plays; },
    };
}

for (const teardown of ["close", "cleanup", "pagehide"]) {
    test(`permission granted after ${teardown} stops every track before starting video`, async () => {
        const app = scanner();
        app.open();
        app[teardown]();
        const stream = camera();
        app.requests[0].resolve(stream);
        await settle();

        assert.deepEqual(stream.trackStops, [1, 1]);
        assert.equal(app.plays, 0);
        assert.equal(app.video.srcObject, null);
        assert.equal(app.intervals.size, 0);
    });
}

test("the first permission result cannot replace the camera from a second open", async () => {
    const app = scanner();
    app.open();
    app.open();
    const first = camera(), second = camera();
    app.requests[1].resolve(second);
    await settle();
    app.requests[0].resolve(first);
    await settle();

    assert.deepEqual(first.trackStops, [1, 1]);
    assert.deepEqual(second.trackStops, [0, 0]);
    assert.equal(app.video.srcObject, second);
    assert.equal(app.plays, 1);
    assert.equal(app.intervals.size, 1);
    app.close();
    assert.deepEqual(second.trackStops, [1, 1]);
    assert.equal(app.video.srcObject, null);
    assert.equal(app.intervals.size, 0);
});

test("playback settling after the sheet closes cannot start a scanner interval", async () => {
    const playing = deferred();
    const app = scanner({ play: () => playing.promise });
    app.open();
    const stream = camera();
    app.requests[0].resolve(stream);
    await settle();
    assert.equal(app.plays, 1);

    app.close();
    playing.resolve();
    await settle();
    assert.deepEqual(stream.trackStops, [1, 1]);
    assert.equal(app.video.srcObject, null);
    assert.equal(app.intervals.size, 0);
});

test("failed playback releases the camera and skips frame polling", async () => {
    const app = scanner({ play: async () => { throw new Error("Cannot play video"); } });
    app.open();
    const stream = camera();
    app.requests[0].resolve(stream);
    await settle();

    assert.deepEqual(stream.trackStops, [1, 1]);
    assert.equal(app.video.srcObject, null);
    assert.equal(app.intervals.size, 0);
});

test("a late detection leaves the replacement camera running and it can scan", async () => {
    const detection = deferred();
    let calls = 0;
    const app = scanner({ detect: () => ++calls === 1 ? detection.promise : [{ rawValue: "4006381333931" }] });
    app.open();
    const first = camera(), second = camera();
    app.requests[0].resolve(first);
    await settle();
    const staleFrame = [...app.intervals.values()][0]();

    app.open();
    app.requests[1].resolve(second);
    await settle();
    detection.resolve([{ rawValue: "1234567890128" }]);
    await staleFrame;

    assert.deepEqual(app.submissions, []);
    assert.deepEqual(first.trackStops, [1, 1]);
    assert.deepEqual(second.trackStops, [0, 0]);
    await [...app.intervals.values()][0]();
    assert.deepEqual(app.submissions, ["4006381333931"]);
    assert.deepEqual(second.trackStops, [1, 1]);
    assert.equal(app.intervals.size, 0);
});
