// Only the public app shell is cached here. Account data and saved audio live in
// the account-scoped IndexedDB stores; configuration and Telegram traffic bypass this cache.
importScripts('./service-worker-assets.js');
const shellCache = 'channelwave-shell-' + self.assetsManifest.version;
const shellAssets = self.assetsManifest.assets
    .filter(asset => ['index.html', 'offline-start.js', 'manifest.webmanifest', 'icon-192.png', 'icon-512.png', 'icon-maskable-512.png', '_content/ChannelWave.UI/brand/channelwave-mark.svg'].includes(asset.url) || asset.url.startsWith('_framework/') ||
        /^_content\/(ChannelWave.UI|ChannelWave.Platform.Browser|Bit.BlazorUI)\/.*\.(js|css|woff2?)$/.test(asset.url))
    .map(asset => new URL(asset.url, self.registration.scope).href);
self.addEventListener('install', event => event.waitUntil((async () => {
    const existed = (await caches.keys()).includes(shellCache);
    const cache = await caches.open(shellCache);
    try {
        await cache.addAll(shellAssets.map(url => new Request(url, { cache: 'reload',
            integrity: self.assetsManifest.assets.find(asset => new URL(asset.url, self.registration.scope).href === url).hash })));
    } catch (error) { if (!existed) await caches.delete(shellCache); throw error; }
    // First install claims immediately. Updates need an idle vote from every tab.
    if (!self.registration.active) await self.skipWaiting();
})()));
self.addEventListener('activate', event => event.waitUntil((async () => {
    // Keep one prior snapshot for in-flight lazy assets during reload. Never touch
    // account databases or caches belonging to other features.
    const previous = (await caches.keys()).filter(key => key.startsWith('channelwave-shell-') && key !== shellCache);
    for (const key of previous.slice(0, -1)) await caches.delete(key);
    await self.clients.claim();
})()));
let updating;
async function appClients() {
    return (await self.clients.matchAll({ type: 'window', includeUncontrolled: true }))
        .filter(client => client.url.startsWith(self.registration.scope));
}
async function ready(client, hold) {
    return await new Promise(resolve => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => finish(false), 2000);
        function finish(value) { clearTimeout(timer); channel.port1.close(); resolve(value); }
        channel.port1.onmessage = event => finish(event.data?.ready === true);
        try { client.postMessage({ type: 'channelwave-update-ready', hold }, [channel.port2]); }
        catch { finish(false); }
    });
}
async function activateUpdate() {
    let clients = [], committed = false;
    try {
        clients = await appClients();
        if (!(await Promise.all(clients.map(client => ready(client, false)))).every(Boolean)) return;
        const latest = await appClients();
        if (latest.length !== clients.length || latest.some(client => !clients.some(old => old.id === client.id))) return;
        if (!(await Promise.all(clients.map(client => ready(client, true)))).every(Boolean)) return;
        await self.skipWaiting(); committed = true;
    } finally {
        if (!committed) for (const client of clients) client.postMessage({ type: 'channelwave-update-release' });
    }
}
self.addEventListener('message', event => {
    if (event.data?.type !== 'channelwave-activate-update' || !event.source?.url?.startsWith(self.registration.scope)) return;
    updating ??= activateUpdate().finally(() => { updating = null; });
    event.waitUntil(updating);
});
const route = new URL('./__channelwave_audio/', self.registration.scope).pathname;
async function ask(client, token, offset, count) {
    return await new Promise((resolve, reject) => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => { channel.port1.close(); reject(new Error('Audio request timed out.')); }, 65000);
        channel.port1.onmessage = event => {
            clearTimeout(timer); channel.port1.close();
            event.data.error ? reject(new Error(event.data.error)) : resolve(event.data);
        };
        client.postMessage({ type: 'channelwave-audio-range', token, offset, count }, [channel.port2]);
    });
}
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);
    if (url.origin !== self.location.origin) return;
    if (!url.pathname.startsWith(route)) {
        if (event.request.method !== 'GET') return;
        if (event.request.mode === 'navigate') {
            // HTML, JavaScript and assemblies must use one consistent snapshot.
            event.respondWith((async () => (await (await caches.open(shellCache)).match(new URL('index.html', self.registration.scope))) || fetch(event.request))());
        } else if (shellAssets.includes(url.href)) {
            event.respondWith((async () => (await (await caches.open(shellCache)).match(event.request)) || fetch(event.request))());
        } else if (url.pathname.startsWith(new URL('_framework/', self.registration.scope).pathname)) {
            event.respondWith((async () => {
                for (const key of (await caches.keys()).filter(key => key.startsWith('channelwave-shell-') && key !== shellCache).reverse()) {
                    const response = await (await caches.open(key)).match(event.request);
                    if (response) return response;
                }
                return fetch(event.request);
            })());
        }
        return;
    }
    event.respondWith((async () => {
        const client = await self.clients.get(event.clientId);
        if (!client || !['GET', 'HEAD'].includes(event.request.method)) return new Response(null, { status: 403 });
        const token = url.pathname.slice(route.length);
        try {
            const meta = await ask(client, token, 0, 0), total = meta.total;
            const range = event.request.headers.get('Range');
            const match = range?.match(/^bytes=(\d*)-(\d*)$/);
            if (range && (!match || !match[1] && !match[2])) return new Response(null, { status: 416, headers: { 'Content-Range': `bytes */${total}` } });
            let start = match ? match[1] ? Number(match[1]) : Math.max(0, total - Number(match[2])) : 0;
            const end = match?.[1] && match[2] ? Math.min(total - 1, Number(match[2])) : total - 1;
            if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start > end || start >= total)
                return new Response(null, { status: 416, headers: { 'Content-Range': `bytes */${total}` } });
            const headers = { 'Content-Type': meta.mime, 'Content-Length': String(end - start + 1), 'Accept-Ranges': 'bytes', 'Cache-Control': 'no-store' };
            if (range) headers['Content-Range'] = `bytes ${start}-${end}/${total}`;
            let canceled = false;
            const body = event.request.method === 'HEAD' ? null : new ReadableStream({
                async pull(controller) {
                    if (start > end) { controller.close(); return; }
                    try {
                        const count = Math.min(65536, end - start + 1);
                        const part = await ask(client, token, start, count);
                        if (canceled) return;
                        if (part.bytes.byteLength !== count) throw new Error('Telegram returned an incomplete audio chunk.');
                        controller.enqueue(new Uint8Array(part.bytes)); start += count;
                    } catch (error) { if (!canceled) controller.error(error); }
                },
                cancel() { canceled = true; }
            });
            return new Response(body, { status: range ? 206 : 200, headers });
        } catch { return new Response('Audio is unavailable. Return to ChannelWave and retry.', { status: 410 }); }
    })());
});
