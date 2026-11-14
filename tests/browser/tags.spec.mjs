import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installLoadingGates } from './fixtures/loading.mjs';
import { savePreparedExport, installTelegramFixture, openLibrary, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const originalMp3 = await fs.readFile(new URL('./fixtures/stream-tone.mp3', import.meta.url));
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=', 'base64');
const prefixLength = bytes => bytes.subarray(0, 3).toString() === 'ID3'
    ? 10 + bytes.subarray(6, 10).reduce((n, b) => (n << 7) | b, 0) + (bytes[3] === 4 && (bytes[5] & 16) ? 10 : 0) : 0;
async function setup(page, playback = 'tags') {
    await installTelegramFixture(page, playback);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page); await expect(page.locator('.track')).toHaveCount(1);
}
async function editor(page) {
    await clickTrackAction(page.locator('.track'), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details' });
    await expect(dialog).toBeVisible(); return dialog;
}
async function read(page, dialog, complete = true) {
    if (complete) await page.evaluate(() => { globalThis.completeFixtureDownload = null; });
    await dialog.getByRole('button', { name: 'Read tags', exact: true }).click();
    if (complete) {
        await expect(dialog.getByRole('button', { name: 'Cancel operation' })).toBeVisible();
        await expect.poll(() => page.evaluate(() => typeof globalThis.completeFixtureDownload)).toBe('function');
        await page.evaluate(() => completeFixtureDownload());
    }
    await expect(dialog.getByRole('button', { name: 'Export tagged copy' })).toBeEnabled();
}

for (const width of [1440, 390]) {
    test(`tag editing exports Unicode and artwork without changing library labels or original audio at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 }); await setup(page);
        expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
        const dialog = await editor(page); await read(page, dialog);
        await dialog.getByLabel('Title', { exact: true }).fill('آهنگ 🎵');
        await dialog.getByLabel('Artist', { exact: true }).fill('العربية فارسی');
        await dialog.getByLabel('Album', { exact: true }).fill('夜');
        await dialog.getByLabel('Year', { exact: true }).fill('2026');
        await dialog.getByLabel('Genre', { exact: true }).fill('Jazz');
        await dialog.getByLabel('Track number / total').fill('2/12');
        await dialog.getByLabel('Disc number / total').fill('1/2');
        await dialog.locator('input[type=file]').setInputFiles({ name: 'cover.png', mimeType: 'image/png', buffer: png });
        await expect(dialog.getByAltText('Embedded artwork preview')).toBeVisible();
        const pending = page.waitForEvent('download');
        await dialog.getByRole('button', { name: 'Export tagged copy' }).click();
        const exported = await fs.readFile(await (await pending).path());
        expect(exported.subarray(prefixLength(exported))).toEqual(originalMp3.subarray(prefixLength(originalMp3)));
        expect(exported.includes(png)).toBe(true);
        expect(exported.includes(Buffer.from('آهنگ 🎵', exported[3] === 4 ? 'utf8' : 'utf16le'))).toBe(true);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await fs.mkdir('artifacts/browser-review', { recursive: true });
        await page.screenshot({ path: `artifacts/browser-review/tags-${width}.png` });
        await dialog.getByRole('button', { name: 'Close details' }).click();
        await expect(page.locator('.track .music-track-title strong')).toHaveText('Café 🎵');
        await expect(page.locator('.track').getByRole('button', { name: 'More actions' })).toBeFocused();
        const originalDownload = page.waitForEvent('download');
        await clickTrackAction(page.locator('.track'), 'Download');
        // Synthetic TDLib resolves the message afresh and reports it incomplete each time.
        await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
        await page.evaluate(() => completeFixtureDownload());
        await savePreparedExport(page);
        expect(await fs.readFile(await (await originalDownload).path())).toEqual(originalMp3);
    });
}

test('local corrections persist through reindexing and offline reload, independently of exported file tags', async ({ page, context }) => {
    await setup(page);
    await clickTrackAction(page, 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    let dialog = await editor(page);
    await dialog.getByLabel('Local title', { exact: true }).fill('Local فارسی');
    await dialog.getByLabel('Local artist', { exact: true }).fill('Local artist');
    await dialog.getByRole('button', { name: 'Save local corrections' }).click();
    await expect(dialog.getByRole('status')).toContainText('Local corrections saved');
    await dialog.getByRole('button', { name: 'Close details' }).click();
    await page.evaluate(async path => {
        const lib = await import(path), source = JSON.parse(await lib.readSources(700))[0], track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, titleOverride: null, performerOverride: null,
            originalTitle: 'Latest source title', originalPerformer: 'Latest source artist', title: 'Latest source title', performer: 'Latest source artist' }]));
    }, modulePath);
    await context.setOffline(true); await page.reload();
    await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
    await expect(page.locator('.track .music-track-title strong')).toHaveText('Local فارسی');
    dialog = await editor(page); await read(page, dialog, false);
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    await dialog.getByLabel('Title', { exact: true }).fill('Export only');
    const pending = page.waitForEvent('download');
    await dialog.getByRole('button', { name: 'Export tagged copy' }).click();
    const exported = await fs.readFile(await (await pending).path());
    expect(exported.subarray(prefixLength(exported))).toEqual(originalMp3.subarray(prefixLength(originalMp3)));
    await dialog.getByRole('button', { name: 'Restore automatic labels' }).click();
    await expect(dialog.getByRole('status')).toContainText('Restored automatic labels');
    await dialog.getByRole('button', { name: 'Close details' }).click();
    await expect(page.locator('.track .music-track-title strong')).toHaveText('Latest source title');
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    const saved = await page.evaluate(async () => {
        const db = await new Promise(resolve => { const r = indexedDB.open('channelwave-library'); r.onsuccess = () => resolve(r.result); });
        const rows = await new Promise(resolve => { const r = db.transaction('offlineAudio').objectStore('offlineAudio').getAll(); r.onsuccess = () => resolve(r.result); });
        const bytes = Array.from(new Uint8Array(await rows[0].blob.arrayBuffer())); db.close(); return bytes;
    });
    expect(Buffer.from(saved)).toEqual(originalMp3);
});

test('unsupported audio is read-only, cancellation closes safely, and protected tracks cannot read file tags', async ({ page }) => {
    await installLoadingGates(page);
    await setup(page, 'normal'); let dialog = await editor(page);
    await dialog.getByRole('button', { name: 'Read tags', exact: true }).click();
    await dialog.getByRole('button', { name: 'Cancel operation' }).click();
    await expect(dialog.getByRole('status')).toContainText('Operation canceled');
    // The cancel button renders before the replacement TDLib request is sent.
    await page.evaluate(() => {
        globalThis.completeFixtureDownload = null;
        fixtureHoldOperation('telegram:downloadFile');
    });
    await dialog.getByRole('button', { name: 'Read tags', exact: true }).click();
    await expect(dialog.getByRole('button', { name: 'Cancel operation' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('telegram:downloadFile'))).toBe(1);
    expect(await page.evaluate(() => globalThis.completeFixtureDownload)).toBeNull();
    await page.evaluate(() => fixtureReleaseOperation('telegram:downloadFile'));
    await expect.poll(() => page.evaluate(() => typeof globalThis.completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    await expect(dialog.getByRole('status')).toContainText('MP3 files only');
    await expect(dialog.getByRole('button', { name: 'Export tagged copy' })).toBeDisabled();
    await page.keyboard.press('Escape'); await expect(dialog).not.toBeVisible();
    await page.evaluate(async path => {
        const lib = await import(path), source = JSON.parse(await lib.readSources(700))[0], track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, origin: { ...track.origin, protected: true } }]));
    }, modulePath);
    await openLibrary(page); dialog = await editor(page);
    await expect(dialog.getByRole('button', { name: 'Read tags', exact: true })).toBeDisabled();
    await expect(dialog.getByRole('button', { name: 'Save local corrections' })).toBeEnabled();
});

test('tag file handles reject cross-account use, cancellation and known protection changes', async ({ page }) => {
    await setup(page);
    await clickTrackAction(page, 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible(); await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    const errors = await page.evaluate(async path => {
        const lib = await import(path), track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        const h = await lib.openTagFile(700, track.id, null, 0), errors = [];
        for (const work of [() => lib.readTagRange(h.token, 701, 0, 10), () => lib.openTagFile(701, track.id, null, 0), () => lib.setLocalMetadata(701, track.id, 'No', null)])
            try { await work(); } catch (e) { errors.push(e.message); }
        const source = JSON.parse(await lib.readSources(700))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, origin: { ...track.origin, protected: true } }]));
        try { await lib.exportTagCopy(h.token, 700, 0, new Uint8Array(), false, null, 'denied.mp3'); } catch (e) { errors.push(e.message); }
        lib.cancelMediaWork();
        try { await lib.readTagRange(h.token, 700, 0, 10); } catch (e) { errors.push(e.message); }
        return errors;
    }, modulePath);
    expect(errors).toHaveLength(5); expect(errors[3]).toContain('restricts saving'); expect(errors[4]).toContain('expired');
});

test('changing only the artist leaves the title inherited from future source updates', async ({ page }) => {
    await setup(page);
    const dialog = await editor(page);
    await dialog.getByLabel('Local artist', { exact: true }).fill('Corrected artist');
    await dialog.getByRole('button', { name: 'Save local corrections' }).click();
    await expect(dialog.getByRole('status')).toContainText('Local corrections saved');
    await dialog.getByRole('button', { name: 'Close details' }).click();
    const stored = await page.evaluate(async path => {
        const lib = await import(path), source = JSON.parse(await lib.readSources(700))[0];
        const before = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...before, title: 'New source title', originalTitle: 'New source title', performer: 'Source artist', originalPerformer: 'Source artist' }]));
        return JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
    }, modulePath);
    expect(stored.titleOverride).toBeNull(); expect(stored.title).toBe('New source title'); expect(stored.performer).toBe('Corrected artist');
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
});

test('JPEG artwork exports unchanged and a corrupt PNG is rejected without losing the draft', async ({ page }) => {
    await setup(page); const dialog = await editor(page); await read(page, dialog);
    await dialog.getByLabel('Title', { exact: true }).fill('Keep this draft');
    const broken = Buffer.from(png); broken[52] ^= 1;
    await dialog.locator('input[type=file]').setInputFiles({ name: 'bad.png', mimeType: 'image/png', buffer: broken });
    await expect(dialog.getByRole('alert')).toContainText('valid JPEG or PNG');
    await expect(dialog.getByLabel('Title', { exact: true })).toHaveValue('Keep this draft');
    const encoded = await page.evaluate(() => {
        const canvas = document.createElement('canvas'); canvas.width = canvas.height = 8;
        const context = canvas.getContext('2d'); context.fillStyle = '#228844'; context.fillRect(0, 0, 8, 8);
        return canvas.toDataURL('image/jpeg').split(',')[1];
    });
    const jpeg = Buffer.from(encoded, 'base64');
    await dialog.locator('input[type=file]').setInputFiles({ name: 'cover.jpg', mimeType: 'image/jpeg', buffer: jpeg });
    await expect(dialog.getByRole('alert')).toHaveCount(0); await expect(dialog.getByAltText('Embedded artwork preview')).toBeVisible();
    const pending = page.waitForEvent('download');
    await dialog.getByRole('button', { name: 'Export tagged copy' }).click();
    const exported = await fs.readFile(await (await pending).path());
    expect(exported.includes(jpeg)).toBe(true);
    expect(exported.subarray(prefixLength(exported))).toEqual(originalMp3.subarray(prefixLength(originalMp3)));
});

// Independently inspect the downloaded ID3 bytes rather than using the application's codec.
function syncSize(value) {
    return Buffer.from([(value >> 21) & 127, (value >> 14) & 127, (value >> 7) & 127, value & 127]);
}
function textFrame(id, value, version) {
    return rawFrame(id, Buffer.concat([Buffer.from([0]), Buffer.from(value, 'latin1')]), version);
}
function rawFrame(id, content, version) {
    const header = Buffer.alloc(10); header.write(id, 0, 'ascii');
    if (version === 4) syncSize(content.length).copy(header, 4); else header.writeUInt32BE(content.length, 4);
    return Buffer.concat([header, content]);
}
function sampleTaggedFile(version) {
    const artwork = type => rawFrame('APIC', Buffer.concat([Buffer.from(`\0image/png\0${String.fromCharCode(type)}${type === 3 ? 'Front' : 'Back'}\0`, 'latin1'), png]), version);
    const frames = [textFrame('TIT2', 'File title', version), textFrame('TPE1', 'File artist', version), textFrame('TALB', 'Old album', version),
        textFrame(version === 4 ? 'TDRC' : 'TYER', '2000', version), textFrame('TCON', 'Blues', version),
        textFrame('TRCK', '1/9', version), textFrame('TPOS', '1', version), rawFrame('XABC', Buffer.from([1, 255, 225, 0, 7]), version), artwork(3), artwork(4)];
    const body = Buffer.concat([...frames, Buffer.alloc(8)]), header = Buffer.alloc(10);
    header.write('ID3'); header[3] = version; syncSize(body.length).copy(header, 6);
    const legacy = Buffer.alloc(128); legacy.write('TAG'); legacy.write('File title', 3); legacy.write('File artist', 33);
    legacy.write('Old album', 63); legacy.write('2000', 93); legacy.write('Preserve my comment', 97); legacy[126] = 1;
    return Buffer.concat([header, body, originalMp3.subarray(prefixLength(originalMp3)), legacy]);
}
function inspectTags(bytes) {
    expect(bytes.subarray(0, 3).toString()).toBe('ID3');
    const version = bytes[3], end = prefixLength(bytes), frames = [], fields = {}, artwork = [];
    for (let offset = 10; offset < end && bytes[offset] !== 0;) {
        const id = bytes.toString('ascii', offset, offset + 4);
        const size = version === 4 ? bytes.subarray(offset + 4, offset + 8).reduce((n, b) => (n << 7) | b, 0) : bytes.readUInt32BE(offset + 4);
        expect(offset + 10 + size).toBeLessThanOrEqual(end);
        const raw = bytes.subarray(offset, offset + 10 + size), content = raw.subarray(10);
        frames.push({ id, raw });
        if (id.startsWith('T')) {
            if (content[0] === 1) {
                expect(content.subarray(1, 3)).toEqual(Buffer.from([255, 254]));
                fields[id] = content.subarray(3).toString('utf16le').replace(/\0+$/u, '');
            } else fields[id] = content.subarray(1).toString(content[0] === 3 ? 'utf8' : 'latin1').replace(/\0+$/u, '');
        }
        if (id === 'APIC') {
            const mimeEnd = content.indexOf(0, 1), type = content[mimeEnd + 1], descriptionStart = mimeEnd + 2;
            const descriptionEnd = content.indexOf(0, descriptionStart);
            artwork.push({ type, data: content.subarray(descriptionEnd + 1) });
        }
        offset += raw.length;
    }
    return { version, frames, fields, artwork };
}
async function supplyCompletedFile(page, bytes) {
    await page.evaluate(encoded => {
        globalThis.tagTestBytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
        if (globalThis.tagTestSendPatched) return;
        globalThis.tagTestSendPatched = true;
        const original = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            const bytes = globalThis.tagTestBytes;
            if (query['@type'] === 'readFile') {
                fixtureCalls.push('readFile');
                return { '@type': 'filePart', data: new Blob([bytes], { type: 'audio/mpeg' }).slice(query.offset ?? 0, (query.offset ?? 0) + (query.count || bytes.length)) };
            }
            if (query['@type'] === 'getFileDownloadedPrefixSize')
                return { '@type': 'fileDownloadedPrefixSize', size: Math.max(0, bytes.length - query.offset) };
            const response = await original.call(this, query);
            // Background source audits must see the same replacement as direct reads.
            const complete = file => {
                const size = globalThis.tagTestBytes.length;
                file.size = file.expected_size = size;
                file.local.is_downloading_completed = true; file.local.is_downloading_active = false; file.local.downloaded_size = size;
            };
            if (query['@type'] === 'getMessage') complete(response.content.audio.audio);
            if (query['@type'] === 'getMessages' || query['@type'] === 'searchChatMessages')
                for (const message of response.messages) if (message?.content?.audio) complete(message.content.audio.audio);
            if (query['@type'] === 'downloadFile') complete(response);
            return response;
        };
    }, bytes.toString('base64'));
    await page.evaluate(async ({ path, size }) => {
        const lib = await import(path), [source] = JSON.parse(await lib.readSources(700));
        const [track] = JSON.parse(await lib.searchLibrary(700, '', 0, 100));
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, media: { ...track.media, size } }]));
    }, { path: modulePath, size: bytes.length });
    await openLibrary(page);
}

for (const version of [3, 4]) {
    test(`web ID3v2.${version} export round-trips every field, preserves unrelated data and decodes as audio`, async ({ page }) => {
        await setup(page);
        const source = sampleTaggedFile(version); await supplyCompletedFile(page, source);
        let dialog = await editor(page); await read(page, dialog, false);
        await expect(dialog.getByLabel('Title', { exact: true })).toHaveValue('File title');
        await expect(dialog.getByLabel('Artist', { exact: true })).toHaveValue('File artist');
        await expect(dialog.getByAltText('Embedded artwork preview')).toBeVisible();
        const values = { Title: 'آهنگ 🎵', Artist: 'العربية فارسی', Album: '夜の音楽', Year: '2026', Genre: 'Jazz', 'Track number / total': '2/12', 'Disc number / total': '1/2' };
        for (const [label, value] of Object.entries(values)) await dialog.getByLabel(label, { exact: true }).fill(value);
        await dialog.getByRole('button', { name: 'Remove selected artwork' }).click();
        await dialog.getByLabel('Year', { exact: true }).fill('invalid');
        let downloads = 0; page.on('download', () => downloads++);
        await dialog.getByRole('button', { name: 'Export tagged copy' }).click();
        await expect(dialog.getByRole('alert')).toContainText('Year must be'); expect(downloads).toBe(0);
        await dialog.getByLabel('Year', { exact: true }).fill('2026');
        const pending = page.waitForEvent('download'); await dialog.getByRole('button', { name: 'Export tagged copy' }).click();
        const download = await pending; expect(download.suggestedFilename()).toBe('fixture-tagged.mp3');
        const exported = await fs.readFile(await download.path());
        const inspected = inspectTags(exported), initial = inspectTags(source);
        expect(inspected.version).toBe(version);
        expect(inspected.fields).toEqual({ TIT2: values.Title, TPE1: values.Artist, TALB: values.Album, [version === 4 ? 'TDRC' : 'TYER']: '2026', TCON: 'Jazz', TRCK: '2/12', TPOS: '1/2' });
        expect(inspected.frames.find(f => f.id === 'XABC').raw).toEqual(initial.frames.find(f => f.id === 'XABC').raw);
        expect(inspected.artwork).toEqual([{ type: 4, data: png }]);
        expect(exported.subarray(prefixLength(exported), exported.length - 128)).toEqual(source.subarray(prefixLength(source), source.length - 128));
        // ID3v1.1 has a 28-byte comment; the final two bytes hold its delimiter and track number.
        expect(exported.subarray(exported.length - 31, exported.length - 3)).toEqual(source.subarray(source.length - 31, source.length - 3));
        expect(exported.toString('ascii', exported.length - 35, exported.length - 31)).toBe('2026');
        expect(exported[exported.length - 2]).toBe(2); expect(exported[exported.length - 1]).toBe(8);
        await fs.mkdir('artifacts/browser-review', { recursive: true });
        await download.saveAs(`artifacts/browser-review/web-tags-v2${version}.mp3`);

        // Export leaves the indexed source labels and original bytes unchanged.
        await dialog.getByRole('button', { name: 'Close details' }).click();
        await expect(page.locator('.track .music-track-title strong')).toHaveText('File title');
        expect(Buffer.from(await page.evaluate(() => Array.from(tagTestBytes)))).toEqual(source);

        // Substituting the export as a new source lets the real editor read it back.
        await supplyCompletedFile(page, exported); dialog = await editor(page); await read(page, dialog, false);
        for (const [label, value] of Object.entries(values)) await expect(dialog.getByLabel(label, { exact: true })).toHaveValue(value);
        await expect(dialog.getByLabel('Artwork', { exact: true }).getByRole('option')).toHaveCount(2);
        await expect(dialog.getByAltText('Embedded artwork preview')).toBeVisible();
        await page.screenshot({ path: `artifacts/browser-review/web-tags-v2${version}.png` });
        const decoded = await page.evaluate(async encoded => {
            const bytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
            const url = URL.createObjectURL(new Blob([bytes], { type: 'audio/mpeg' })), audio = new Audio(url); audio.muted = true;
            try {
                await new Promise((resolve, reject) => {
                    const timeout = setTimeout(() => reject(new Error('Exported MP3 did not decode')), 10000);
                    audio.onloadedmetadata = () => { clearTimeout(timeout); resolve(); };
                    audio.onerror = () => { clearTimeout(timeout); reject(new Error('Exported MP3 is not playable')); };
                    audio.load();
                });
                await audio.play(); await new Promise(resolve => setTimeout(resolve, 250));
                return { duration: audio.duration, advanced: audio.currentTime > 0 };
            } finally { audio.pause(); audio.removeAttribute('src'); audio.load(); URL.revokeObjectURL(url); }
        }, exported.toString('base64'));
        expect(decoded.duration).toBeGreaterThan(29); expect(decoded.duration).toBeLessThan(31); expect(decoded.advanced).toBe(true);
        await dialog.getByRole('button', { name: 'Close details' }).click();
        await expect(page.locator('.track .music-track-title strong')).toHaveText(values.Title);
    });
}
