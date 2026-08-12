// Deliberately conservative: this app is Blazor Server, so the live UI needs the network.
// The service worker exists to make the app installable and to show a branded offline page
// instead of the browser's error page when the connection drops.
const CACHE = 'ai-shop-shell-v2';
const SHELL = ['/app.css', '/icon-192.png', '/icon-512.png', '/manifest.webmanifest', '/offline.html'];

self.addEventListener('install', event => {
    event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
            .then(() => self.clients.claim()));
});

self.addEventListener('fetch', event => {
    const request = event.request;
    if (request.method !== 'GET') return;

    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    // Never cache the SignalR circuit, the MCP endpoint, or file downloads.
    if (url.pathname.startsWith('/_blazor') || url.pathname.startsWith('/mcp') || url.pathname.startsWith('/files')) return;

    if (SHELL.includes(url.pathname)) {
        event.respondWith(caches.match(request).then(cached => cached || fetch(request)));
        return;
    }

    if (request.mode === 'navigate') {
        event.respondWith(fetch(request).catch(() => caches.match('/offline.html')));
    }
});
