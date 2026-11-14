import { test, expect } from '@playwright/test';
import http from 'node:http';
import fs from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { installTelegramFixture } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const versionHeading = (page, version) => page.getByRole('heading', { name: `Shell version ${version}`, exact: true });
test.beforeEach(async ({ page }) => installTelegramFixture(page));
async function fixture() {
    const [worker, startup, adapter] = await Promise.all([
        fs.readFile('src/ChannelWave.Host.Web/wwwroot/audio-worker.js'),
        fs.readFile('src/ChannelWave.Host.Web/wwwroot/offline-start.js'),
        fs.readFile('src/ChannelWave.Platform.Browser/wwwroot/channelwave.js')
    ]);
    const state = { version: 1, broken: false };
    const hash = bytes => 'sha256-' + createHash('sha256').update(bytes).digest('base64');
    const server = http.createServer(async (req, res) => {
        const html = `<!doctype html><html><head><link rel="stylesheet" href="/_content/ChannelWave.UI/music.css"></head><body><h1>Shell version ${state.version}</h1><div class="music-seek-track"></div><dialog id="editor"><input aria-label="Playlist draft"><button>Save</button></dialog><script src="/offline-start.js"></script></body></html>`;
        const assets = [
            { url: 'index.html', bytes: html }, { url: 'offline-start.js', bytes: startup },
            { url: modulePath.slice(1), bytes: adapter },
            { url: '_content/ChannelWave.UI/music.css', bytes: `.music-seek-track { --fixture-version: ${state.version}; }` },
            { url: `_framework/lazy-v${state.version}.wasm`, bytes: `synthetic assembly v${state.version}` }
        ];
        const pathname = new URL(req.url, 'http://localhost').pathname;
        res.setHeader('Cache-Control', 'no-store');
        if (pathname === '/audio-worker.js') { await state.workerGate; res.setHeader('Content-Type', 'text/javascript'); res.end(worker); }
        else if (pathname === '/service-worker-assets.js') {
            res.setHeader('Content-Type', 'text/javascript');
            res.end('self.assetsManifest = ' + JSON.stringify({ version: String(state.version), assets: assets.map(a => ({ url: a.url, hash: hash(a.bytes) })) }));
        } else {
            const asset = assets.find(a => '/' + a.url === pathname || a.url === 'index.html' && pathname === '/');
            if (!asset) { res.writeHead(404); res.end(); return; }
            res.setHeader('Content-Type', pathname.endsWith('.js') ? 'text/javascript' : pathname.endsWith('.css') ? 'text/css' : pathname.endsWith('.wasm') ? 'application/wasm' : 'text/html');
            res.end(state.broken && pathname.endsWith('.css') ? 'broken integrity' : asset.bytes);
        }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    return { state, base: `http://127.0.0.1:${server.address().port}`, close: () => new Promise(resolve => server.close(resolve)) };
}
async function open(page, base) {
    await page.goto(base);
    await page.evaluate(() => channelwaveOfflineReady);
    await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);
}
async function check(page) { await page.evaluate(() => window.dispatchEvent(new Event('online'))); }
async function waiting(page) {
    await expect.poll(() => page.evaluate(async () => !!(await navigator.serviceWorker.getRegistration()).waiting), { timeout: 15000 }).toBe(true);
}
async function updated(page, version) {
    await expect(versionHeading(page, version)).toBeVisible({ timeout: 20000 });
    await expect.poll(() => page.evaluate(() => getComputedStyle(document.querySelector('.music-seek-track')).getPropertyValue('--fixture-version').trim())).toBe(String(version));
}
async function seedMusic(page) {
    return await page.evaluate(async p => {
        const lib = await import(p);
        await lib.startTelegram({ invokeMethodAsync: async () => {} });
        const source = { accountId: 700, chatId: -100123, name: 'Synthetic source', historyComplete: true };
        const track = { id: crypto.randomUUID(), identity: '700:-100123:1', origin: { accountId: 700, chatId: source.chatId, messageId: 1, sourceName: source.name, protected: false },
            media: { remoteId: 'fixture', uniqueId: 'fixture', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
            title: 'Saved song', performer: 'Fixture artist', duration: 5, favorite: true, searchText: 'saved song fixture artist' };
        await lib.savePage(JSON.stringify(source), JSON.stringify([track]));
        await lib.saveState(700, 'playlist', JSON.stringify({ Id: crypto.randomUUID(), Name: 'Listening list', Tracks: [track.id] }));
        await lib.saveState(700, 'playlists', JSON.stringify([{ Id: crypto.randomUUID(), Name: 'Night drive', Tracks: [track.id] }]));
        await lib.saveState(701, 'playlists', 'other account stays');
        await lib.saveOffline(JSON.stringify(track), 1, 80044);
        await new Promise((resolve, reject) => {
            const request = indexedDB.open('/channelwave-telegram/dbfs', 1);
            request.onupgradeneeded = () => request.result.createObjectStore('session');
            request.onerror = () => reject(request.error);
            request.onsuccess = () => {
                const db = request.result, tx = db.transaction('session', 'readwrite');
                tx.objectStore('session').put('synthetic session sentinel', 'saved');
                tx.oncomplete = () => { db.close(); resolve(); };
            };
        });
        localStorage.setItem('fixture-setting', 'keep');
        await (await caches.open('account-audio-sentinel')).put('/keep', new Response('keep this cache'));
        const copy = await lib.openOffline(700, track.id);
        const audioBytes = await (await fetch(copy.location)).arrayBuffer();
        URL.revokeObjectURL(copy.location);
        const digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', audioBytes))].map(b => b.toString(16).padStart(2, '0')).join('');
        return { id: track.id, digest };
    }, modulePath);
}
async function assertMusic(page, id, expectedDigest) {
    const saved = await page.evaluate(async ({ p, id }) => {
        const lib = await import(p);
        const file = await lib.openOffline(700, id);
        const bytes = new Uint8Array(await (await fetch(file.location)).arrayBuffer());
        URL.revokeObjectURL(file.location);
        const session = await new Promise((resolve, reject) => {
            const request = indexedDB.open('/channelwave-telegram/dbfs');
            request.onerror = () => reject(request.error);
            request.onsuccess = () => {
                const db = request.result, read = db.transaction('session').objectStore('session').get('saved');
                read.onsuccess = () => { db.close(); resolve(read.result); };
            };
        });
        return { tracks: JSON.parse(await lib.searchLibrary(700, '', 0, 10)), offline: JSON.parse(await lib.offlineTracks(700)),
            listening: JSON.parse(await lib.readState(700, 'playlist')), playlists: JSON.parse(await lib.readState(700, 'playlists')),
            other: await lib.readState(701, 'playlists'), bytes: bytes.length, signature: [...bytes.slice(0, 4)], session,
            digest: [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(b => b.toString(16).padStart(2, '0')).join(''),
            setting: localStorage.getItem('fixture-setting'), cache: await (await (await caches.open('account-audio-sentinel')).match('/keep')).text() };
    }, { p: modulePath, id });
    expect(saved.tracks[0]).toMatchObject({ id, favorite: true });
    expect(saved.offline[0].id).toBe(id);
    expect(saved.listening.Tracks).toEqual([id]);
    expect(saved.playlists[0]).toMatchObject({ Name: 'Night drive', Tracks: [id] });
    expect(saved.other).toBe('other account stays');
    expect(saved.bytes).toBe(80044);
    expect(saved.signature).toEqual([82, 73, 70, 70]);
    expect(saved.digest).toBe(expectedDigest);
    expect(saved.session).toBe('synthetic session sentinel');
    expect(saved.setting).toBe('keep');
    expect(saved.cache).toBe('keep this cache');
}

test('first installation does not swallow actions during an activation vote', async ({ page }) => {
    const f = await fixture();
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    // Browser service-worker script fetches bypass Playwright page routing.
    f.state.workerGate = pending;
    try {
        await page.clock.install();
        await page.goto(f.base);
        await expect.poll(() => page.evaluate(() => !!window.channelwaveOfflineReady)).toBe(true);
        await page.clock.fastForward(2000);
        const result = await page.evaluate(async () => {
            if (navigator.serviceWorker.controller) throw new Error('Expected a first installation');
            const button = document.createElement('button');
            let clicks = 0;
            button.addEventListener('click', () => clicks++); document.body.append(button);
            const channel = new MessageChannel();
            const vote = new Promise(resolve => { channel.port1.onmessage = event => resolve(event.data); });
            navigator.serviceWorker.dispatchEvent(new MessageEvent('message', {
                data: { type: 'channelwave-update-ready', hold: true }, ports: [channel.port2]
            }));
            const ready = (await vote).ready;
            button.click(); channel.port1.close();
            return { ready, clicks };
        });
        expect(result).toEqual({ ready: true, clicks: 1 });
    } finally { release(); await page.unrouteAll({ behavior: 'wait' }); await f.close(); }
});

test('a late activation vote from the current worker cannot swallow input', async ({ page }) => {
    const f = await fixture();
    try {
        await page.clock.install();
        await open(page, f.base);
        await page.clock.fastForward(2000);
        const result = await page.evaluate(async () => {
            const button = document.createElement('button');
            let clicks = 0;
            button.addEventListener('click', () => clicks++); document.body.append(button);
            const channel = new MessageChannel();
            const vote = new Promise(resolve => { channel.port1.onmessage = event => resolve(event.data); });
            navigator.serviceWorker.dispatchEvent(new MessageEvent('message', {
                data: { type: 'channelwave-update-ready', hold: true },
                source: navigator.serviceWorker.controller, ports: [channel.port2]
            }));
            const ready = (await vote).ready;
            button.click(); channel.port1.close();
            return { ready, clicks };
        });
        expect(result).toEqual({ ready: true, clicks: 1 });
    } finally { await f.close(); }
});

test('automatically reloads an idle shell and preserves real adapter music, playlists and session stores', async ({ page, context }) => {
    const f = await fixture();
    try {
        await open(page, f.base);
        const { id, digest } = await seedMusic(page);
        f.state.version = 2;
        await check(page);
        await updated(page, 2);
        await expect.poll(() => page.evaluate(async () => (await navigator.serviceWorker.getRegistration()).updateViaCache)).toBe('none');
        await assertMusic(page, id, digest);
        await context.setOffline(true);
        await page.reload();
        await updated(page, 2);
        await assertMusic(page, id, digest);
        await page.evaluate(async ({ p, id }) => {
            const lib = await import(p), file = await lib.openOffline(700, id);
            await lib.loadAudio(file.location); await lib.playAudio();
        }, { p: modulePath, id });
        await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(0);
        await page.evaluate(async p => (await import(p)).clearAudio(), modulePath);
    } finally { await f.close(); }
});

test('waits for paused media to be stopped, retains old lazy assets, and still supports offline saves while waiting', async ({ page }) => {
    const f = await fixture();
    try {
        await open(page, f.base);
        const { id } = await seedMusic(page);
        await page.evaluate(async ({ p, id }) => {
            const lib = await import(p), file = await lib.openOffline(700, id);
            await lib.loadAudio(file.location); await lib.playAudio(); lib.pauseAudio();
        }, { p: modulePath, id });
        f.state.version = 2;
        await check(page); await waiting(page);
        await expect(versionHeading(page, 1)).toBeVisible();
        expect(await page.evaluate(() => window.dispatchEvent(new Event('channelwave-before-update', { cancelable: true })))).toBe(false);
        expect(await page.evaluate(async () => (await fetch('/_framework/lazy-v1.wasm')).text())).toBe('synthetic assembly v1');
        expect(await page.evaluate(async () => {
            await new Promise(resolve => { const script = document.createElement('script'); script.src = '/offline-start.js'; script.onload = resolve; document.head.append(script); });
            return !!(await channelwaveOfflineReady)?.active;
        })).toBe(true);
        await page.evaluate(async p => (await import(p)).clearAudio(), modulePath);
        await updated(page, 2);
        expect(await page.evaluate(async () => (await fetch('/_framework/lazy-v1.wasm')).text())).toBe('synthetic assembly v1');
    } finally { await f.close(); }
});

test('coordinates open tabs and protects an unsaved editor before switching the entire shell snapshot', async ({ page, context }) => {
    const f = await fixture();
    try {
        await open(page, f.base);
        const other = await context.newPage(); await open(other, f.base);
        await other.evaluate(() => document.querySelector('dialog').showModal());
        await other.getByLabel('Playlist draft').fill('Unsaved playlist name');
        f.state.version = 2;
        await check(page); await waiting(page);
        await expect(versionHeading(page, 1)).toBeVisible();
        await expect(other.getByLabel('Playlist draft')).toHaveValue('Unsaved playlist name');
        const third = await context.newPage(); await open(third, f.base);
        await expect(versionHeading(third, 1)).toBeVisible();
        await third.close();
        await other.evaluate(() => { document.querySelector('dialog').close(); document.activeElement?.blur(); });
        await updated(page, 2); await updated(other, 2);
    } finally { await f.close(); }
});

test('an incomplete or corrupt update leaves the current shell and saved music usable offline, then retries online', async ({ page, context }) => {
    const f = await fixture();
    try {
        await open(page, f.base);
        const { id, digest } = await seedMusic(page);
        await page.evaluate(async () => {
            const registration = await navigator.serviceWorker.getRegistration();
            registration.addEventListener('updatefound', () => {
                const worker = registration.installing;
                worker.addEventListener('statechange', () => { if (worker.state === 'redundant') window.fixtureFailedInstall = true; });
            });
        });
        f.state.version = 2; f.state.broken = true;
        await check(page);
        await expect.poll(() => page.evaluate(() => !!window.fixtureFailedInstall), { timeout: 15000 }).toBe(true);
        await expect(versionHeading(page, 1)).toBeVisible();
        await expect.poll(() => page.evaluate(async () => (await caches.keys()).filter(key => key.startsWith('channelwave-shell-')))).toEqual(['channelwave-shell-1']);
        await context.setOffline(true); await page.reload();
        await updated(page, 1); await assertMusic(page, id, digest);
        f.state.broken = false;
        await context.setOffline(false); await check(page);
        await updated(page, 2); await assertMusic(page, id, digest);
    } finally { await f.close(); }
});

for (const activity of ['account work', 'download']) {
    test(`an active ${activity} defers activation until it finishes`, async ({ page }) => {
        const f = await fixture();
        try {
            await page.clock.install();
            await open(page, f.base);
            await page.evaluate(activity => {
                const busy = document.createElement('div'); busy.id = 'fixture-busy';
                if (activity === 'account work') busy.dataset.updateBusy = 'true';
                else busy.className = 'music-transfer';
                document.body.append(busy);
            }, activity);
            f.state.version = 2;
            await page.clock.fastForward(60000);
            await waiting(page);
            await expect(versionHeading(page, 1)).toBeVisible();
            await page.evaluate(() => document.getElementById('fixture-busy').remove());
            await page.clock.fastForward(5000);
            await updated(page, 2);
        } finally { await f.close(); }
    });
}

test('checks periodically, reloads once per version, and keeps only the current and previous public shells', async ({ page }) => {
    const f = await fixture();
    try {
        await page.clock.install();
        await open(page, f.base);
        let reloads = 0;
        page.on('framenavigated', frame => { if (frame === page.mainFrame()) ++reloads; });
        f.state.version = 2;
        await page.clock.fastForward(60000);
        await updated(page, 2);
        expect(reloads).toBe(1);
        f.state.version = 3;
        await page.clock.fastForward(60000);
        await updated(page, 3);
        expect(reloads).toBe(2);
        await expect.poll(() => page.evaluate(async () => (await caches.keys()).filter(key => key.startsWith('channelwave-shell-')).sort())).toEqual(['channelwave-shell-2', 'channelwave-shell-3']);
        expect(await page.evaluate(async () => (await fetch('/_framework/lazy-v2.wasm')).text())).toBe('synthetic assembly v2');
    } finally { await f.close(); }
});
