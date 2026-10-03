// Caches static files only (SPEC section 8). Pages and data always come from the server;
// when a page can't be fetched, the offline page is shown instead.
const CACHE = "gym-static-v6";
const OFFLINE = "/offline.html";
const PRECACHE = [
    OFFLINE,
    "/manifest.webmanifest",
    "/icons/icon-192.png",
    "/icons/icon-512.png",
    "/icons/apple-touch-icon.png",
    "/icons/icon-32.png",
    "/css/tokens.css",
    "/css/app.css",
    "/js/i18n.js",
    "/js/motion.js",
    "/js/site.js",
    "/js/session.js",
    "/js/progress.js",
    "/js/scanner.js",
    "/lib/barcode-detector/barcode-detector-3.2.2.ponyfill.js",
    "/lib/barcode-detector/zxing_reader.wasm",
    "/lib/htmx/htmx.min.js",
    "/lib/chartjs/chart.umd.min.js",
    "/lib/fonts/fonts.css",
    "/lib/fonts/fira-sans-400-latin.woff2",
    "/lib/fonts/fira-sans-400-cyrillic.woff2",
    "/lib/fonts/fira-sans-500-latin.woff2",
    "/lib/fonts/fira-sans-500-cyrillic.woff2",
    "/lib/fonts/fira-sans-600-latin.woff2",
    "/lib/fonts/fira-sans-600-cyrillic.woff2",
];

self.addEventListener("install", (event) => {
    event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim()));
});

function isStatic(url) {
    return url.origin === self.location.origin && /^\/(css|js|lib)\//.test(url.pathname);
}

self.addEventListener("fetch", (event) => {
    const request = event.request;
    if (request.method !== "GET") {
        return;
    }

    const url = new URL(request.url);

    if (isStatic(url)) {
        // Serve from cache straight away (version query strings ignored), refresh in the background.
        event.respondWith(caches.open(CACHE).then(async (cache) => {
            const cached = await cache.match(request, { ignoreSearch: true });
            const network = fetch(request)
                .then((response) => {
                    if (response.ok) {
                        cache.put(url.pathname, response.clone());
                    }
                    return response;
                })
                .catch(() => cached);
            return cached || network;
        }));
        return;
    }

    if (request.mode === "navigate") {
        event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
    }
});
