// Caches static files only (SPEC section 8). Pages and data always come from the server;
// when a page can't be fetched, the offline page is shown instead. Static files carry ?v=<hash>,
// so a cached copy of the exact URL is served without revalidating.
const CACHE = "gym-static-v12";
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
    "/js/pages.js",
    "/js/motion.js",
    "/js/site.js",
    "/js/session.js",
    "/js/nav.js",
    "/js/offline.js",
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

async function staticResponse(request, url) {
    const cache = await caches.open(CACHE);
    // Exact URL first: versioned URLs (?v=hash) never change, so no revalidation is needed.
    const exact = await cache.match(request);
    if (exact) {
        return exact;
    }
    try {
        const response = await fetch(request);
        if (response.ok) {
            const keys = await cache.keys();
            await Promise.all(keys
                .filter((k) => new URL(k.url).pathname === url.pathname && k.url !== request.url)
                .map((k) => cache.delete(k)));
            await cache.put(request, response.clone());
        }
        return response;
    } catch {
        // Offline and this version was never fetched: any cached copy of the file beats nothing.
        return (await cache.match(request, { ignoreSearch: true })) || Response.error();
    }
}

self.addEventListener("fetch", (event) => {
    const request = event.request;
    if (request.method !== "GET") {
        return;
    }

    const url = new URL(request.url);

    if (isStatic(url)) {
        event.respondWith(staticResponse(request, url));
        return;
    }

    if (request.mode === "navigate") {
        event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
    }
});
