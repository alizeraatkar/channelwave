import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, openLibrary, openChannels, clickTrackAction, stopPlayback, connectFixture } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const original = await fs.readFile(new URL('./fixtures/stream-tone.mp3', import.meta.url));
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=', 'base64');
function frame(id, bytes) { const header = Buffer.alloc(10); header.write(id); header.writeUInt32BE(bytes.length, 4); return Buffer.concat([header, bytes]); }
const body = Buffer.concat([
    frame('TIT2', Buffer.concat([Buffer.from([3]), Buffer.from('Automatic فارسی')])),
    frame('TPE1', Buffer.from('\x03Embedded artist')),
    frame('TALB', Buffer.from('\x03Automatic album')),
    frame('APIC', Buffer.concat([Buffer.from('\0image/png\0\x03\0'), png]))
]);
const header = Buffer.alloc(10); header.write('ID3'); header[3] = 3;
for (let i = 3, size = body.length; i >= 0; i--, size >>>= 7) header[6 + i] = size & 127;
const oldPrefix = original.subarray(0, 3).toString() === 'ID3' ? 10 + original.subarray(6, 10).reduce((n, b) => (n << 7) | b, 0) : 0;
const audio = Buffer.concat([header, body, original.subarray(oldPrefix)]);

async function setup(page, { paused = false, cached = false, hold = false, fail = false } = {}) {
    await installTelegramFixture(page, 'tags', { automaticMetadata: true });
    await page.addInitScript(({ encoded, cached, hold, fail }) => {
        const bytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
        const originalSend = tdweb.default.prototype.send;
        globalThis.metadataRequests = []; globalThis.metadataReads = []; globalThis.metadataCanceled = 0;
        globalThis.metadataFail = fail;
        let ranges = cached ? [{ offset: 0, limit: bytes.length }] : [], pending;
        tdweb.default.prototype.send = async function (query) {
            const type = query['@type'];
            if (type === 'getFileDownloadedPrefixSize') {
                if (metadataFail) return { '@type': 'error', code: 503, message: 'Synthetic temporary metadata failure' };
                const size = Math.max(0, ...ranges.filter(r => r.offset <= query.offset && r.offset + r.limit > query.offset).map(r => r.offset + r.limit - query.offset));
                return { '@type': 'fileDownloadedPrefixSize', size };
            }
            if (type === 'downloadFile' && query.priority === 1) {
                metadataRequests.push(query);
                if (hold && metadataRequests.length === 1) await new Promise(resolve => {
                    pending = resolve; globalThis.releaseMetadataRequest = resolve;
                });
                ranges.push(query);
                return { '@type': 'file', id: 1, size: bytes.length, local: {}, remote: { id: 'fixture', unique_id: 'fixture-unique' } };
            }
            if (type === 'cancelDownloadFile') { metadataCanceled++; pending?.(); pending = null; }
            if (type === 'readFile' && query.file_id === 1) {
                metadataReads.push({ offset: query.offset, count: query.count });
                return { '@type': 'filePart', data: new Blob([bytes], { type: 'audio/mpeg' }).slice(query.offset ?? 0, query.count ? query.offset + query.count : bytes.length) };
            }
            const response = await originalSend.call(this, query);
            if (type === 'downloadFile') {
                response.size = response.expected_size = bytes.length;
                if (!query.synchronous) globalThis.completeFixtureDownload = () => this.options.onUpdate({ '@type': 'updateFile', file: { ...response, local: { ...response.local, is_downloading_completed: true, downloaded_size: bytes.length } } });
            }
            const update = message => { message.content.audio.audio.size = message.content.audio.audio.expected_size = bytes.length; };
            if (type === 'getMessage') update(response);
            if (type === 'getMessages') response.messages.filter(Boolean).forEach(update);
            if (type === 'searchChatMessages') response.messages.forEach(update);
            return response;
        };
    }, { encoded: audio.toString('base64'), cached, hold, fail });
    await page.goto('/'); await connectFixture(page);
    if (paused) {
        await openRoot(page, 'Settings');
        await page.getByRole('button', { name: 'Pause metadata loading' }).click();
        await openChannels(page);
    }
    await page.getByRole('button', { name: 'Add to library: :)', exact: true }).click();
    await openLibrary(page); await expect(page.locator('.track')).toHaveCount(1);
}
const stored = page => page.evaluate(async path => JSON.parse(await (await import(path)).searchLibrary(700, '', 0, 100))[0], modulePath);

test('automatically reads bounded tag ranges and artwork without opening details or downloading the audio', async ({ page }) => {
    await setup(page);
    await expect(page.locator('.track .music-track-title strong')).toHaveText('Automatic فارسی', { timeout: 20000 });
    await expect(page.locator('.track .music-art img')).toBeVisible();
    const track = await stored(page);
    expect(track.embeddedMetadata.album).toBe('Automatic album'); expect(track.metadataScan.status).toBe(0);
    const requests = await page.evaluate(() => metadataRequests);
    expect(requests.length).toBeGreaterThan(0); expect(requests.length).toBeLessThanOrEqual(128);
    expect(requests.every(r => r.limit > 0 && r.limit <= 1024 * 1024 && r.priority === 1)).toBe(true);
    expect(requests.reduce((n, r) => n + r.limit, 0)).toBeLessThan(audio.length);
    expect(await page.evaluate(() => document.querySelector('.metadata-editor') !== null)).toBe(false);
    await page.reload();
    await expect.poll(async () => (await stored(page))?.metadataScan?.status).toBe(0);
    expect(await page.evaluate(() => metadataRequests)).toEqual([]);
});

test('identical audio shares one scan and its cached fields can be edited before opening the MP3', async ({ page }) => {
    await setup(page, { cached: true });
    await expect.poll(async () => (await stored(page))?.embeddedMetadata?.album, { timeout: 15000 }).toBe('Automatic album');
    const initialReads = await page.evaluate(() => metadataReads.length);
    const duplicateId = await page.evaluate(async path => {
        const lib = await import(path);
        const source = JSON.parse(await lib.readSources(700))[0];
        const [track] = JSON.parse(await lib.searchLibrary(700, '', 0, 100));
        const id = crypto.randomUUID(), messageId = track.origin.messageId + 10;
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, id,
            origin: { ...track.origin, messageId }, identity: `700:${source.chatId}:${messageId}`,
            media: { ...track.media, runtimeId: 42 }, artwork: null, embeddedMetadata: null, metadataScan: null }]));
        return id;
    }, modulePath);
    await expect.poll(async () => page.evaluate(async ({ path, duplicateId }) => {
        const lib = await import(path);
        return JSON.parse(await lib.searchLibrary(700, '', 0, 100)).find(t => t.id === duplicateId)?.embeddedMetadata?.album;
    }, { path: modulePath, duplicateId }), { timeout: 15000 }).toBe('Automatic album');
    expect(await page.evaluate(() => metadataReads.length)).toBe(initialReads);

    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(2);
    await clickTrackAction(page.locator('.track').first(), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details' });
    await expect(dialog.getByLabel('Title', { exact: true })).toHaveValue('Automatic فارسی');
    await dialog.getByLabel('Title', { exact: true }).fill('Export draft');
    await expect(dialog.getByRole('button', { name: 'Export tagged copy' })).toBeEnabled();
    expect(await page.evaluate(() => metadataReads.length)).toBe(initialReads);
    await dialog.getByRole('button', { name: 'Close details' }).click();
    await clickTrackAction(page.locator('.track').first(), 'Edit details');
    await expect(dialog.getByLabel('Title', { exact: true })).toHaveValue('Automatic فارسی');
    expect(await page.evaluate(() => metadataReads.length)).toBe(initialReads);
    await dialog.getByLabel('Title', { exact: true }).fill('Export draft');
    const pending = page.waitForEvent('download');
    await dialog.getByRole('button', { name: 'Export tagged copy' }).click();
    await expect(dialog.getByRole('button', { name: 'Cancel operation' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => typeof globalThis.completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    const exported = await fs.readFile(await (await pending).path());
    expect(exported.includes(frame('TALB', Buffer.from('\x03Automatic album')))).toBe(true);
    expect(exported.includes(png)).toBe(true);
    expect((await stored(page)).embeddedMetadata.title).toBe('Automatic فارسی');
});

test('pause persists and enrichment preserves manual labels, favorites and source originals', async ({ page }) => {
    await setup(page, { paused: true, cached: true });
    await page.evaluate(async path => {
        const lib = await import(path), track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        await lib.setLocalMetadata(700, track.id, 'Manual correction', null); await lib.favorite(700, track.id, true);
    }, modulePath);
    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openRoot(page, 'Settings');
    await expect(page.getByRole('button', { name: 'Resume metadata loading' })).toBeVisible();
    expect((await stored(page)).metadataScan).toBeFalsy();
    await page.getByRole('button', { name: 'Resume metadata loading' }).click();
    await openLibrary(page);
    await expect.poll(async () => (await stored(page)).embeddedMetadata?.artist).toBe('Embedded artist');
    const track = await stored(page); expect(track.title).toBe('Manual correction'); expect(track.favorite).toBe(true); expect(track.originalTitle).toBe('Café 🎵');
    expect(await page.evaluate(() => metadataRequests)).toEqual([]);
    await clickTrackAction(page.locator('.track'), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details' });
    await expect(dialog.getByRole('region', { name: 'Automatically loaded metadata' })).toContainText('Automatic album');
    await dialog.getByRole('button', { name: 'Restore automatic labels' }).click();
    await dialog.getByRole('button', { name: 'Close details' }).click();
    await expect(page.locator('.track .music-track-title strong')).toHaveText('Automatic فارسی');
});

test('automatic tags arriving in an open editor preserve a local correction in progress', async ({ page }) => {
    await setup(page, { hold: true });
    await expect.poll(() => page.evaluate(() => typeof globalThis.releaseMetadataRequest), { timeout: 15000 }).toBe('function');
    await clickTrackAction(page.locator('.track'), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details' });
    await dialog.getByLabel('Local artist', { exact: true }).fill('My artist correction');
    await page.evaluate(() => releaseMetadataRequest());
    await expect(dialog.getByLabel('Local title', { exact: true })).toHaveValue('Automatic فارسی');
    await expect(dialog.getByLabel('Local artist', { exact: true })).toHaveValue('My artist correction');
    await dialog.getByRole('button', { name: 'Save local corrections' }).click();
    await expect.poll(async () => (await stored(page))?.performerOverride).toBe('My artist correction');
    expect((await stored(page)).titleOverride).toBeNull();
});

test('foreground streaming drains a held metadata request and survives background cancellation', async ({ page }) => {
    await setup(page, { hold: true });
    await expect.poll(() => page.evaluate(() => metadataRequests.length), { timeout: 15000 }).toBe(1);
    await clickTrackAction(page.locator('.track'), 'Stream');
    await expect.poll(() => page.evaluate(() => metadataCanceled)).toBe(1);
    await expect(page.getByRole('button', { name: 'Pause playback', exact: true })).toBeVisible({ timeout: 20000 });
    const before = await page.evaluate(() => metadataRequests.length);
    await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(0.1);
    expect(await page.evaluate(() => metadataRequests.length)).toBe(before);
    await stopPlayback(page);
    await expect.poll(async () => (await stored(page)).metadataScan?.status, { timeout: 15000 }).toBe(0);
});

test('IndexedDB rejects stale and cross-account results and retains metadata through reindexing', async ({ page }) => {
    await setup(page, { paused: true });
    const result = await page.evaluate(async path => {
        const lib = await import(path), source = JSON.parse(await lib.readSources(700))[0], track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        const key = `${track.media.uniqueId}:${track.media.size}`, fields = JSON.stringify({ title: 'Embedded', artist: 'Artist', album: 'Album' });
        const scan = JSON.stringify({ mediaKey: key, readerVersion: 1, status: 0, attempts: 1 });
        await Promise.all([lib.favorite(700, track.id, true), lib.setLocalMetadata(700, track.id, 'Manual', null), lib.setMetadataScan(700, track.id, key, fields, scan)]);
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, id: crypto.randomUUID(), originalTitle: 'Fresh source' }]));
        const saved = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        const other = await lib.setMetadataScan(701, track.id, key, fields, scan);
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, media: { ...track.media, size: track.media.size + 1 } }]));
        const stale = await lib.setMetadataScan(700, track.id, key, fields, scan);
        await lib.removeSource(700, source.chatId);
        const removed = await lib.setMetadataScan(700, track.id, key, fields, scan);
        return { saved, other, stale, removed };
    }, modulePath);
    expect(result.saved.title).toBe('Manual'); expect(result.saved.performer).toBe('Artist'); expect(result.saved.favorite).toBe(true);
    expect(result.saved.embeddedMetadata.album).toBe('Album'); expect(result.other).toBeNull(); expect(result.stale).toBeNull(); expect(result.removed).toBeNull();
});

test('automatic scanning reuses the saved offline Blob without requesting Telegram ranges', async ({ page }) => {
    await setup(page, { paused: true });
    await clickTrackAction(page.locator('.track'), 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1);
    await page.evaluate(() => { metadataReads.length = 0; fixtureCalls.length = 0; });
    await openRoot(page, 'Settings');
    await page.getByRole('button', { name: 'Resume metadata loading' }).click();
    await expect.poll(async () => (await stored(page)).embeddedMetadata?.album).toBe('Automatic album');
    expect(await page.evaluate(() => metadataRequests)).toEqual([]);
    expect(await page.evaluate(() => metadataReads)).toEqual([]);
    expect(await page.evaluate(() => fixtureCalls.includes('getMessage'))).toBe(false);
});

test('transient failures stop after three attempts and can be retried from account settings', async ({ page }) => {
    await setup(page, { fail: true });
    await expect.poll(async () => (await stored(page)).metadataScan?.attempts, { timeout: 25000 }).toBe(3);
    expect((await stored(page)).metadataScan.status).toBe(3);
    await page.evaluate(() => { metadataFail = false; });
    await openRoot(page, 'Settings');
    await page.getByRole('button', { name: 'Retry failed metadata' }).click();
    await expect.poll(async () => (await stored(page)).metadataScan?.status).toBe(0);
    await openLibrary(page);
    await expect(page.locator('.track .music-track-title strong')).toHaveText('Automatic فارسی');
    await openRoot(page, 'Settings');
    await page.getByRole('region', { name: 'Automatic metadata' }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: 'artifacts/automatic-metadata-account.png' });
});
