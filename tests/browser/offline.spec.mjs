import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture, openLibrary, openChannels, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
async function setup(page) {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(1);
}
async function save(page) {
    await clickTrackAction(page.locator('.track'), 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    await expect(page.locator('.music-feedback')).toContainText('Saved Café 🎵 offline');
}
for (const width of [1440, 390]) {
    test(`saved audio survives offline reload and plays without Telegram at ${width}px`, async ({ page, context }) => {
        await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
        let exported = false; page.on('download', () => exported = true);
        await setup(page); await save(page);
        expect(exported).toBe(false);
        // Disable networking before reload, eliminating both Telegram and the dev server.
        await context.setOffline(true);
        await page.reload();
        await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
        await expect(page.locator('.track')).toHaveCount(1);
        await page.locator('.track').getByRole('button', { name: 'Play', exact: true }).click();
        const player = page.getByRole('region', { name: 'Now playing', exact: true });
        await expect(player.getByRole('status')).toContainText('Playing offline');
        await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(0);
        await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
        const seek = player.getByRole('slider', { name: 'Playback position' });
        await seek.fill('2'); await seek.dispatchEvent('change');
        await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeCloseTo(2, 0);
        await player.getByRole('button', { name: 'Resume playback', exact: true }).click();
        await expect(player.getByRole('status')).toContainText('Playing offline');
        await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
        expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.screenshot({ path: `artifacts/browser-review/offline-${width}.png` });
        // Removal stops the current audio and persists across another offline reload.
        await clickTrackAction(page, 'Remove offline copy');
        await expect(page.locator('.track')).toHaveCount(0);
        await expect(player).toHaveCount(0);
        await page.reload();
        await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Take your music with you' })).toBeVisible();
        await expect(page.getByRole('alert')).toHaveCount(0);
    });
}
test('saved copies are preferred in the online library and removed with their source', async ({ page }) => {
    await setup(page); await save(page);
    await page.evaluate(() => { fixtureCalls.length = 0; });
    await page.locator('.track').getByRole('button', { name: 'Play', exact: true }).click();
    await expect(page.locator('.music-player-status')).toHaveText('Playing offline');
    expect(await page.evaluate(() => fixtureCalls.filter(x => ['getMessage', 'downloadFile', 'readFile'].includes(x)))).toEqual([]);
    await openChannels(page);
    await page.getByRole('button', { name: 'Remove from library: :)', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeEnabled();
    const saved = await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath);
    expect(saved).toEqual([]);
});
test('canceling a save and a storage failure never claim an offline copy exists', async ({ page }) => {
    await setup(page);
    await clickTrackAction(page, 'Save offline');
    await page.getByRole('button', { name: 'Cancel download' }).click();
    await expect(page.locator('.music-feedback')).toContainText('Offline save canceled');
    await page.evaluate(() => {
        const transaction = IDBDatabase.prototype.transaction;
        IDBDatabase.prototype.transaction = function (names, mode, ...rest) {
            if (mode === 'readwrite' && Array.from(names).includes('offlineAudio')) throw new DOMException('Device storage is full. Free space and retry.', 'QuotaExceededError');
            return transaction.call(this, names, mode, ...rest);
        };
    });
    await clickTrackAction(page, 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.getByRole('alert')).toContainText('storage is full');
    await expect(page.getByRole('button', { name: 'Remove offline copy' })).toHaveCount(0);
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
});
test('sign out erases offline metadata and audio bytes for the account', async ({ page }) => {
    await setup(page); await save(page);
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data' }).click();
    await expect(page.locator('p.status')).toContainText('erased');
    const counts = await page.evaluate(async () => {
        const db = await new Promise(resolve => { const r = indexedDB.open('channelwave-library'); r.onsuccess = () => resolve(r.result); });
        const counts = await Promise.all(['offlineTracks', 'offlineAudio'].map(name => new Promise(resolve => { const r = db.transaction(name).objectStore(name).count(); r.onsuccess = () => resolve(r.result); })));
        db.close(); return counts;
    });
    expect(counts).toEqual([0, 0]);
});
test('truncated Telegram audio cannot be saved offline', async ({ page }) => {
    await setup(page);
    await page.evaluate(() => {
        const send = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            const response = await send.call(this, query);
            return query['@type'] === 'readFile' ? { ...response, data: response.data.slice(0, 100) } : response;
        };
    });
    await clickTrackAction(page, 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.getByRole('alert')).toContainText('100 of 80044');
    await expect(page.getByRole('button', { name: 'Remove offline copy' })).toHaveCount(0);
});

test('cancel during the final Blob read prevents a late offline write', async ({ page }) => {
    await setup(page);
    await page.evaluate(() => {
        const send = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            const response = await send.call(this, query);
            if (query['@type'] === 'readFile') await new Promise(resolve => { globalThis.releaseOfflineRead = resolve; });
            return response;
        };
    });
    await clickTrackAction(page, 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect.poll(() => page.evaluate(() => typeof releaseOfflineRead)).toBe('function');
    await page.getByRole('button', { name: 'Cancel download' }).click();
    await expect(page.locator('.music-feedback')).toContainText('Offline save canceled');
    await page.evaluate(() => releaseOfflineRead());
    await expect(page.locator('.music-feedback')).toContainText('Offline save canceled');
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
    await expect(page.getByRole('button', { name: 'Remove offline copy' })).toHaveCount(0);
});

test('version-one libraries upgrade without losing indexed metadata', async ({ page }) => {
    // Seed the old schema before the app's startup cleanup check opens the library.
    await page.goto('/channelwave.example.json');
    await page.evaluate(async () => {
        const db = await new Promise((resolve, reject) => {
            const r = indexedDB.open('channelwave-library', 1);
            r.onupgradeneeded = () => {
                const tracks = r.result.createObjectStore('tracks', { keyPath: ['origin.accountId', 'identity'] });
                tracks.createIndex('account', 'origin.accountId'); tracks.createIndex('accountId', ['origin.accountId', 'id'], { unique: true });
                const sources = r.result.createObjectStore('sources', { keyPath: ['accountId', 'chatId'] }); sources.createIndex('account', 'accountId');
                const state = r.result.createObjectStore('state', { keyPath: ['account', 'key'] }); state.createIndex('account', 'account');
            };
            r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error);
        });
        await new Promise(resolve => {
            const tx = db.transaction('state', 'readwrite');
            tx.objectStore('state').put({ account: 700, key: 'playlist', value: 'keep this playlist' }); tx.oncomplete = resolve;
        });
        db.close();
    });
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeVisible();
    const result = await page.evaluate(async path => {
        const lib = await import(path); await lib.openLibrary();
        return { saved: JSON.parse(await lib.offlineTracks(700)), playlist: await lib.readState(700, 'playlist') };
    }, modulePath);
    expect(result).toEqual({ saved: [], playlist: 'keep this playlist' });
});
