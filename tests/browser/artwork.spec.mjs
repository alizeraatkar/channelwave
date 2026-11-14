import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { deflateSync } from 'node:zlib';
import { installTelegramFixture, openLibrary, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const mp3 = await fs.readFile(new URL('./fixtures/stream-tone.mp3', import.meta.url));
function image() {
    const chunk = (type, bytes) => {
        const name = Buffer.from(type), length = Buffer.alloc(4), crc = Buffer.alloc(4); length.writeUInt32BE(bytes.length);
        let value = 0xffffffff;
        for (const byte of Buffer.concat([name, bytes])) { value ^= byte; for (let i = 0; i < 8; i++) value = value & 1 ? (value >>> 1) ^ 0xedb88320 : value >>> 1; }
        crc.writeUInt32BE((value ^ 0xffffffff) >>> 0); return Buffer.concat([length, name, bytes, crc]);
    };
    const header = Buffer.alloc(13); header.writeUInt32BE(80); header.writeUInt32BE(80, 4); header[8] = 8; header[9] = 2;
    const pixels = Buffer.alloc(80 * (1 + 80 * 3));
    for (let y = 0; y < 80; y++) for (let x = 0; x < 80; x++) { const i = y * 241 + 1 + x * 3; pixels[i] = 30 + x; pixels[i + 1] = 100 + y; pixels[i + 2] = 100; }
    return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))]);
}
const png = image();
const oldPrefix = 10 + mp3.subarray(6, 10).reduce((n, b) => (n << 7) | b, 0);
const picture = Buffer.concat([Buffer.from([0]), Buffer.from('image/png\0'), Buffer.from([3, 0]), png]);
const frame = Buffer.alloc(10); frame.write('APIC'); frame.writeUInt32BE(picture.length, 4);
const tag = Buffer.alloc(10); tag.write('ID3'); tag[3] = 3;
for (let i = 3, size = frame.length + picture.length; i >= 0; i--, size >>>= 7) tag[6 + i] = size & 127;
const embeddedMp3 = Buffer.concat([tag, frame, picture, mp3.subarray(oldPrefix)]);

async function setup(page, options = {}) {
    page.on('pageerror', error => console.error(error.message));
    page.on('console', message => { if (message.type() === 'error') console.error(message.text()); });
    await installTelegramFixture(page, options.protected ? 'protected' : 'tags');
    await page.addInitScript(({ options, audio, image }) => {
        const audioBytes = Uint8Array.from(atob(audio), c => c.charCodeAt(0)), imageBytes = Uint8Array.from(atob(image), c => c.charCodeAt(0));
        const original = tdweb.default.prototype.send;
        globalThis.artworkQueries = [];
        tdweb.default.prototype.send = async function (query) {
            artworkQueries.push({ ...query });
            if (query['@type'] === 'getMessage' && globalThis.holdArtwork) await new Promise(resolve => { globalThis.releaseArtwork = resolve; });
            const small = id => ({ '@type': 'file', id, size: imageBytes.length, expected_size: imageBytes.length,
                local: { is_downloading_completed: false, downloaded_size: 0 }, remote: { id: `image-${id}`, unique_id: `image-${id}` } });
            if (query['@type'] === 'readFile') {
                if (query.file_id !== 1 && options.delayImage) await (globalThis.imageGate ??= new Promise(resolve => { globalThis.releaseImage = resolve; }));
                const bytes = query.file_id === 1 ? audioBytes : imageBytes;
                return { '@type': 'filePart', data: new Blob([bytes]).slice(query.offset || 0, (query.offset || 0) + (query.count || bytes.length)) };
            }
            if (query['@type'] === 'downloadFile' && query.file_id !== 1) return { ...small(query.file_id), local: { is_downloading_completed: true, downloaded_size: imageBytes.length } };
            const response = await original.call(this, query);
            if (query['@type'] === 'downloadFile' && query.file_id === 1) {
                response.size = response.expected_size = audioBytes.length;
                if (!query.synchronous) globalThis.completeFixtureDownload = () => this.options.onUpdate({ '@type': 'updateFile',
                    file: { ...response, local: { ...response.local, is_downloading_completed: true, downloaded_size: audioBytes.length } } });
            }
            const update = message => {
                message.content.audio.audio.size = message.content.audio.audio.expected_size = audioBytes.length;
                if (options.embedded) message.content.audio.audio.local = { is_downloading_completed: true, downloaded_size: audioBytes.length };
                if (options.thumbnail && (query['@type'] !== 'getMessage' || query.message_id === 1)) message.content.audio.album_cover_thumbnail = { '@type': 'thumbnail', file: small(2) };
            };
            if (query['@type'] === 'getMessage') update(response);
            if (query['@type'] === 'getMessages') for (const message of response.messages.filter(Boolean)) {
                message.content.audio.audio.size = message.content.audio.audio.expected_size = audioBytes.length;
            }
            if (query['@type'] === 'searchChatMessages') response.messages.forEach(update);
            if (query['@type'] === 'getChat' && options.channel) response.photo = { '@type': 'chatPhotoInfo', small: small(3) };
            return response;
        };
    }, { options, audio: (options.embedded || options.embeddedOnPlayback ? embeddedMp3 : mp3).toString('base64'), image: png.toString('base64') });
    await page.goto('/'); await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page); await expect(page.locator('.track')).toHaveCount(1);
}
const cover = page => page.locator('.track .music-art img');
async function stored(page) { return page.evaluate(async path => JSON.parse(await (await import(path)).searchLibrary(700, '', 0, 100))[0], modulePath); }

test('a refreshed row displays newly persisted artwork without changing media identity', async ({ page }) => {
    await setup(page);
    await expect(cover(page)).toHaveCount(0);
    const uri = await page.evaluate(async ({ path, image }) => {
        const lib = await import(path);
        const track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        await lib.setArtwork(700, track.id, JSON.stringify({
            mediaKey: `${track.media.uniqueId || track.media.remoteId}:${track.media.size}`, kind: 0, data: image
        }));
        return `data:image/png;base64,${image}`;
    }, { path: modulePath, image: png.toString('base64') });
    // Refresh parameters while retaining the keyed TrackArtwork component.
    await page.locator('.music-search').getByRole('button', { name: 'Search', exact: true }).click();
    await expect(cover(page)).toHaveAttribute('src', uri);
});

for (const width of [1440, 390]) {
    test(`embedded cover beats thumbnails, persists and stays independent of tag drafts at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 }); await setup(page, { embedded: true, thumbnail: true, channel: true });
        await expect(cover(page)).toBeVisible(); await expect(cover(page)).toHaveAttribute('alt', 'Cover artwork for Café 🎵');
        expect((await stored(page)).artwork.kind).toBe(0);
        expect(await page.evaluate(() => artworkQueries.filter(x => x['@type'] === 'downloadFile'))).toEqual([]);
        const ranges = await page.evaluate(() => artworkQueries.filter(x => x['@type'] === 'readFile'));
        expect(ranges.every(x => x.offset === 0 && x.count <= 10 + frame.length + picture.length)).toBe(true);
        const uri = await cover(page).getAttribute('src');
        await clickTrackAction(page.locator('.track'), 'Edit details');
        const dialog = page.getByRole('dialog', { name: 'Edit details' });
        await dialog.getByRole('button', { name: 'Read tags', exact: true }).click();
        await expect(dialog.getByAltText('Embedded artwork preview')).toBeVisible();
        await dialog.getByRole('button', { name: 'Remove selected artwork' }).click();
        await dialog.getByRole('button', { name: 'Close details' }).click();
        await expect(cover(page)).toHaveAttribute('src', uri);
        await page.reload(); await openRoot(page, 'Profile'); await connectFixture(page); await openLibrary(page);
        await expect(cover(page)).toHaveAttribute('src', uri);
        expect(await page.evaluate(() => artworkQueries.filter(x => ['downloadFile', 'readFile'].includes(x['@type'])))).toEqual([]);
        await fs.mkdir('artifacts/browser-review', { recursive: true }); await page.screenshot({ path: `artifacts/browser-review/artwork-${width}.png` });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    });
}

test('Telegram thumbnail and labelled channel-photo fallback transfer only small images', async ({ page }) => {
    await setup(page, { thumbnail: true, channel: true }); await expect(cover(page)).toBeVisible();
    expect((await stored(page)).artwork.kind).toBe(1);
    expect(await page.evaluate(() => artworkQueries.filter(x => x['@type'] === 'downloadFile').map(x => x.file_id))).toEqual([2]);
    expect(await page.evaluate(() => artworkQueries.filter(x => x['@type'] === 'readFile').map(x => x.file_id))).toEqual([2]);
});

test('channel photo is identified honestly and missing or protected artwork keeps the music icon', async ({ page }) => {
    await setup(page, { channel: true }); await expect(cover(page)).toHaveAttribute('alt', 'Source photo for :)');
    expect((await stored(page)).artwork.kind).toBe(2);
    expect(await page.evaluate(() => artworkQueries.filter(x => x['@type'] === 'downloadFile').map(x => x.file_id))).toEqual([3]);
    await page.evaluate(async path => { const lib = await import(path), track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0], source = JSON.parse(await lib.readSources(700))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, origin: { ...track.origin, protected: true } }]));
    }, modulePath);
    await openLibrary(page); await expect(cover(page)).toHaveCount(0); await expect(page.locator('.track .music-art .music-icon')).toBeVisible();
    expect((await stored(page)).artwork).toBeNull();
});

test('saved embedded cover and source audio survive offline reload without Telegram requests', async ({ page, context }) => {
    await setup(page, { embedded: true }); await expect(cover(page)).toBeVisible();
    await clickTrackAction(page, 'Save offline');
    await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    const uri = await cover(page).getAttribute('src');
    await context.setOffline(true); await page.reload(); await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
    await expect(cover(page)).toHaveAttribute('src', uri); expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    const bytes = await page.evaluate(async () => {
        const db = await new Promise(resolve => { const r = indexedDB.open('channelwave-library'); r.onsuccess = () => resolve(r.result); });
        const rows = await new Promise(resolve => { const r = db.transaction('offlineAudio').objectStore('offlineAudio').getAll(); r.onsuccess = () => resolve(r.result); }); db.close();
        return Array.from(new Uint8Array(await rows[0].blob.arrayBuffer()));
    }); expect(Buffer.from(bytes)).toEqual(embeddedMp3);
});

test('IndexedDB artwork writes preserve concurrent corrections, enforce bounds and reject stale or foreign rows', async ({ page }) => {
    await setup(page, { embedded: true }); await expect(cover(page)).toBeVisible();
    const result = await page.evaluate(async path => {
        const lib = await import(path), source = JSON.parse(await lib.readSources(700))[0], track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        await Promise.all([lib.favorite(700, track.id, true), lib.setLocalMetadata(700, track.id, 'Local', null), lib.setArtwork(700, track.id, JSON.stringify(track.artwork))]);
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, id: crypto.randomUUID(), originalTitle: 'Latest source' }]));
        const merged = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0], errors = [];
        for (const work of [() => lib.setArtwork(701, track.id, JSON.stringify(track.artwork)), () => lib.setArtwork(700, track.id, JSON.stringify({ ...track.artwork, mediaKey: 'stale' }))])
            try { await work(); } catch (e) { errors.push(e.message); }
        const many = Array.from({ length: 65 }, (_, i) => ({ ...track, id: crypto.randomUUID(), identity: `700:${source.chatId}:${i + 2}`, origin: { ...track.origin, messageId: i + 2 }, media: { ...track.media, uniqueId: `image-${i}` } }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(many));
        for (const row of many) await lib.setArtwork(700, row.id, JSON.stringify({ ...track.artwork, mediaKey: `${row.media.uniqueId}:${row.media.size}` }));
        const count = JSON.parse(await lib.searchLibrary(700, '', 0, 100)).filter(x => x.artwork).length;
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, media: { ...track.media, uniqueId: 'replacement' } }]));
        const replaced = JSON.parse(await lib.searchLibrary(700, '', 0, 100)).find(x => x.id === track.id);
        await lib.removeSource(700, source.chatId);
        try { await lib.setArtwork(700, track.id, JSON.stringify(track.artwork)); } catch (e) { errors.push(e.message); }
        return { merged, count, replaced, errors, remaining: JSON.parse(await lib.searchLibrary(700, '', 0, 100)).length };
    }, modulePath);
    expect(result.merged.id).toBeTruthy(); expect(result.merged.favorite).toBe(true); expect(result.merged.title).toBe('Local'); expect(result.merged.artwork.kind).toBe(0);
    expect(result.count).toBe(64); expect(result.replaced.artwork).toBeNull(); expect(result.errors).toHaveLength(3); expect(result.remaining).toBe(0);
});

test('a cover discovered during playback reaches system metadata without restarting audio', async ({ page }) => {
    await setup(page, { embeddedOnPlayback: true });
    await expect(cover(page)).toHaveCount(0);
    await page.locator('.track').getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('button', { name: 'Pause playback' })).toBeEnabled();
    await expect(player.locator('.music-art img')).toBeVisible();
    const uri = await player.locator('.music-art img').getAttribute('src');
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata?.artwork[0]?.src)).toBe(uri);
    await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(0.1);
    const before = await page.evaluate(() => fixtureAudio.currentTime);
    await clickTrackAction(page.locator('.track'), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details' });
    await dialog.getByRole('button', { name: 'Read tags', exact: true }).click();
    await expect(dialog.getByRole('button', { name: 'Cancel operation' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(dialog.getByAltText('Embedded artwork preview')).toBeVisible();
    expect(await page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThanOrEqual(before);
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata?.artwork[0]?.src)).toBe(uri);
    await dialog.getByRole('button', { name: 'Close details' }).click();
    await player.getByRole('button', { name: 'Stop playback', exact: true }).click();
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata)).toBeNull();
});

test('Now Playing uses the source cover and local corrections update labels without restarting audio', async ({ page }) => {
    await setup(page, { embedded: true }); await expect(cover(page)).toBeVisible();
    await page.locator('.track').getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('button', { name: 'Pause playback' })).toBeEnabled();
    await expect(player.locator('.music-art img')).toHaveAttribute('src', await cover(page).getAttribute('src'));
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata?.artwork[0]?.src)).toBe(await cover(page).getAttribute('src'));
    await clickTrackAction(page.locator('.track'), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details' });
    await dialog.getByLabel('Local title', { exact: true }).fill('Corrected title'); await dialog.getByRole('button', { name: 'Save local corrections' }).click();
    await expect(dialog.getByRole('status')).toContainText('Local corrections saved'); await dialog.getByRole('button', { name: 'Close details' }).click();
    await expect(player.locator('.music-player-info strong')).toHaveText('Corrected title');
    await expect(player.getByRole('button', { name: 'Pause playback' })).toBeEnabled();
    await expect(player.locator('.music-art img')).toHaveAttribute('alt', 'Cover artwork for Corrected title');
    expect(await page.evaluate(() => navigator.mediaSession.metadata.title)).toBe('Corrected title');
});

test('delayed artwork refreshes browser metadata without restarting playback and clears on stop', async ({ page }) => {
    await setup(page, { thumbnail: true, delayImage: true });
    await expect.poll(() => page.evaluate(() => typeof releaseImage)).toBe('function');
    await page.locator('.track').getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('button', { name: 'Pause playback' })).toBeEnabled();
    expect(await page.evaluate(() => navigator.mediaSession.metadata.artwork.length)).toBe(0);
    const source = await page.evaluate(() => fixtureAudio.src);
    await page.evaluate(() => releaseImage());
    await expect(player.locator('.music-art img')).toBeVisible();
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata.artwork[0]?.src)).toBe(await player.locator('.music-art img').getAttribute('src'));
    expect(await page.evaluate(() => fixtureAudio.src)).toBe(source);
    await player.getByRole('button', { name: 'Pause playback' }).click();
    await expect.poll(() => page.evaluate(() => fixtureAudio.paused)).toBe(true);
    await player.getByRole('button', { name: 'Resume playback' }).click();
    await expect.poll(() => page.evaluate(() => fixtureAudio.paused)).toBe(false);
    await player.getByRole('button', { name: 'Stop playback' }).click();
    expect(await page.evaluate(() => navigator.mediaSession.metadata)).toBeNull();
});

test('a previous track cover finishing after a rapid switch cannot replace the current fallback', async ({ page }) => {
    await setup(page, { thumbnail: true, delayImage: true });
    await expect.poll(() => page.evaluate(() => typeof releaseImage)).toBe('function');
    await page.evaluate(async path => {
        const lib = await import(path), track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        const source = JSON.parse(await lib.readSources(700))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, id: crypto.randomUUID(),
            identity: '700:-100123:2', origin: { ...track.origin, messageId: 2 }, originalTitle: 'No cover', title: 'No cover' }]));
    }, modulePath);
    await openLibrary(page); await expect(page.locator('.track')).toHaveCount(2);
    const first = page.locator('.track').filter({ hasText: 'Café' });
    const second = page.locator('.track').filter({ hasText: 'No cover' });
    await first.getByRole('button', { name: 'Stream', exact: true }).click();
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata?.title)).toBe('Café 🎵');
    await second.getByRole('button', { name: 'Stream', exact: true }).click();
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata?.title)).toBe('No cover');
    await page.evaluate(() => releaseImage());
    await expect(first.locator('.music-art img')).toBeVisible();
    expect(await page.evaluate(() => navigator.mediaSession.metadata.artwork.length)).toBe(0);
    await expect(page.getByRole('region', { name: 'Now playing', exact: true }).locator('.music-art img')).toHaveCount(0);
    // Switching back to the now-cached cover must also clear it on the next uncovered song.
    await first.getByRole('button', { name: 'Stream', exact: true }).click();
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata?.artwork[0]?.src)).toBe(await first.locator('.music-art img').getAttribute('src'));
    await second.getByRole('button', { name: 'Stream', exact: true }).click();
    await expect.poll(() => page.evaluate(() => navigator.mediaSession.metadata?.title)).toBe('No cover');
    expect(await page.evaluate(() => navigator.mediaSession.metadata.artwork.length)).toBe(0);
});

test('playback controls still work when Media Session metadata is unsupported', async ({ page }) => {
    await page.addInitScript(() => Object.defineProperty(navigator, 'mediaSession', { value: undefined }));
    await setup(page, { embedded: true });
    await page.locator('.track').getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('button', { name: 'Pause playback' })).toBeEnabled();
    await player.getByRole('button', { name: 'Pause playback' }).click();
    await expect.poll(() => page.evaluate(() => fixtureAudio.paused)).toBe(true);
    await player.getByRole('button', { name: 'Resume playback' }).click();
    await expect.poll(() => page.evaluate(() => fixtureAudio.paused)).toBe(false);
    await player.getByRole('button', { name: 'Stop playback' }).click();
    await expect(player).toHaveCount(0);
});

test('late cover work cannot recreate a removed source and passive handles cannot export audio', async ({ page }) => {
    await installTelegramFixture(page, 'tags');
    await page.goto('/'); await connectFixture(page);
    await page.getByRole('button', { name: 'Add to library: :)', exact: true }).click();
    await page.evaluate(() => {
        const send = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            if (query['@type'] === 'getMessage') await new Promise(resolve => { globalThis.releaseArtwork = resolve; });
            return send.call(this, query);
        };
    });
    await openLibrary(page); await expect(page.locator('.track')).toHaveCount(1);
    await expect.poll(() => page.evaluate(() => typeof releaseArtwork)).toBe('function');
    const errors = await page.evaluate(async path => {
        const lib = await import(path), track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0], errors = [];
        const handle = await lib.openCachedTagFile(700, track.id, 1, track.media.size);
        try { await lib.exportTagCopy(handle.token, 700, 0, new Uint8Array(), false, null, 'forbidden.mp3'); } catch (e) { errors.push(e.message); }
        lib.cancelMediaWork();
        try { await lib.readTagRange(handle.token, 700, 0, 10); } catch (e) { errors.push(e.message); }
        await lib.removeSource(700, track.origin.chatId); releaseArtwork(); return errors;
    }, modulePath);
    expect(errors[0]).toContain('passive artwork reader'); expect(errors[1]).toContain('expired');
    await openLibrary(page); await expect(page.locator('.track')).toHaveCount(0); expect(await stored(page)).toBeUndefined();
});
