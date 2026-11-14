import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { seed } from './fixtures/redesign.mjs';
import { openRoot, openChannels, clickTrackAction } from './fixtures/telegram.mjs';
import { installLoadingGates } from './fixtures/loading.mjs';

const bridge = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const sourceName = 'Night Radio · رادیو شب';
const picker = page => page.getByRole('dialog', { name: 'Add from Library', exact: true });
const panel = page => page.locator('#home-tab-content');
async function setup(page, count = 125) {
    await seed(page);
    await page.evaluate(async ({ bridge, sourceName, count }) => {
        const lib = await import(bridge);
        const source = { accountId: 700, chatId: -100124, name: sourceName, historyComplete: true };
        const tracks = Array.from({ length: count }, (_, i) => ({ ...discoveryTracks[0], id: crypto.randomUUID(),
            identity: `700:-100124:${String(i + 1).padStart(6, '0')}`,
            origin: { ...discoveryTracks[0].origin, chatId: source.chatId, messageId: i + 1, sourceName },
            title: `Song ${String(i + 1).padStart(5, '0')}` }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        await lib.savePage(JSON.stringify({ ...source, accountId: 800, name: 'Other account only' }), JSON.stringify([
            { ...tracks[0], id: crypto.randomUUID(), identity: '800:-100124:1', origin: { ...tracks[0].origin, accountId: 800 } }
        ]));
        await lib.savePage(JSON.stringify({ ...source, chatId: -100126, name: 'Empty source' }), '[]');
        globalThis.sourceTracks = tracks;
        // Preserve fixture authentication through reloads without real Telegram data.
        await new Promise((resolve, reject) => {
            const request = indexedDB.open('/channelwave-telegram/dbfs', 1);
            request.onsuccess = () => { request.result.close(); resolve(); }; request.onerror = () => reject(request.error);
        });
    }, { bridge, sourceName, count });
    await openRoot(page, 'Home');
}
async function openPicker(page) {
    await page.getByRole('button', { name: 'Add from Library', exact: true }).click();
    await picker(page).getByLabel('Item type').selectOption('Source');
}
async function pinSource(page, name = sourceName) {
    await page.getByRole('button', { name: 'Edit Home tabs', exact: true }).click();
    await openPicker(page);
    await picker(page).getByRole('button', { name, exact: true }).click();
    await expect(picker(page)).not.toBeVisible();
    await page.getByRole('button', { name: 'Done', exact: true }).click();
}

for (const width of [390, 1440]) {
    test(`source tab five-step flow, isolation, pagination and persistence at ${width}px`, async ({ page }) => {
        test.setTimeout(120000);
        await page.setViewportSize({ width, height: 900 });
        await installLoadingGates(page);
        await setup(page);
        await fs.mkdir('artifacts/home-sources', { recursive: true });
        const shot = step => page.screenshot({ path: `artifacts/home-sources/${step}-${width}.png` });
        await expect(page.locator('.app-topbar .brand')).toHaveCount(1);
        await expect(page.locator('.music-home .home-heading')).toHaveCount(0);
        await page.getByRole('tab', { name: 'New', exact: true }).click();
        await shot('01-home');
        await page.getByRole('button', { name: 'Edit Home tabs', exact: true }).click();
        await shot('02-editor');
        await openPicker(page);
        await expect(picker(page).locator('.picker-item')).toHaveText(['Café Sessions · موسیقی', 'Empty source', sourceName]);
        await picker(page).getByLabel('Find an item').fill('no matching source');
        await expect(picker(page)).toContainText('No matching sources');
        await picker(page).getByLabel('Find an item').fill('NIGHT');
        await expect(picker(page).locator('.picker-item')).toHaveCount(1);
        await shot('03-picker');
        await picker(page).getByRole('button', { name: sourceName, exact: true }).click();
        await expect(picker(page)).not.toBeVisible();
        await expect(page.locator('.tab-editor-row').last()).toContainText(sourceName);
        await expect(page.locator('.tab-editor-row').last()).toContainText('Source');
        await shot('04-added');
        await openPicker(page);
        await expect(picker(page).getByRole('button', { name: sourceName, exact: true })).toBeDisabled();
        await page.keyboard.press('Escape');
        await page.getByRole('button', { name: 'Done', exact: true }).click();
        await expect(page.getByRole('tab', { name: 'New', exact: true })).toHaveAttribute('aria-selected', 'true');
        await page.getByRole('tab', { name: sourceName, exact: true }).click();
        await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
        await shot('05-source');
        await panel(page).locator('.home-track').first().getByRole('button', { name: 'Play', exact: true }).click();
        await expect(page.locator('.music-player-status')).toContainText('Streaming', { timeout: 15000 });
        const queueRequest = await page.evaluate(() => fixturePageRequests.find(request => request.idsOnly && request.chat === -100124));
        expect(queueRequest.account).toBe(700);
        expect(queueRequest.limit).toBe(2147483647);
        await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
        await expect(panel(page).locator('.home-track-label small')).toHaveText(
            Array(await panel(page).locator('.home-track-label small').count()).fill(sourceName));
        await page.getByRole('button', { name: 'List view', exact: true }).click();
        await page.locator('#main-content').evaluate(el => el.scrollTop = el.scrollHeight);
        await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '125');
        const reads = await page.evaluate(() => fixturePageRequests.filter(request => request.chat === -100124));
        expect(reads.some(request => request.offset === 100)).toBe(true);
        expect(reads.every(request => request.account === 700)).toBe(true);
        await page.getByRole('tab', { name: 'All', exact: true }).click();
        const preview = page.getByRole('region', { name: sourceName, exact: true });
        await preview.scrollIntoViewIfNeeded();
        await expect(preview.locator('.home-track').first()).toBeVisible();
        await page.reload();
        await expect(page.getByRole('tab', { name: sourceName, exact: true })).toBeVisible();
        await page.getByRole('tab', { name: sourceName, exact: true }).click();
        await expect(page.getByRole('button', { name: 'List view', exact: true })).toHaveAttribute('aria-pressed', 'true');
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    });
}

test('empty sources remain valid and unindexing removes the tab without recreating it', async ({ page }) => {
    const removedName = 'Café Sessions · موسیقی';
    await setup(page, 3);
    await pinSource(page, 'Empty source');
    await page.getByRole('tab', { name: 'Empty source', exact: true }).click();
    await expect(panel(page)).toContainText('No music in this collection yet.');
    await expect(panel(page).getByRole('button', { name: 'Remove shortcut' })).toHaveCount(0);
    await pinSource(page, removedName);
    await page.getByRole('tab', { name: removedName, exact: true }).click();
    await openChannels(page);
    await page.getByRole('button', { name: `Remove from library: ${removedName}`, exact: true }).click();
    await expect(page.getByRole('region', { name: 'Choose a source' })).toContainText(`Unindexed ${removedName}`);
    await openRoot(page, 'Home');
    await expect(page.getByRole('tab', { name: removedName, exact: true })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: 'All', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('tab', { name: 'Empty source', exact: true })).toBeVisible();
    await page.evaluate(async ({ bridge, removedName }) => {
        await (await import(bridge)).savePage(JSON.stringify({ accountId: 700, chatId: -100123, name: removedName }), '[]');
    }, { bridge, removedName });
    await page.reload();
    await expect(page.getByRole('tab', { name: 'Empty source', exact: true })).toBeVisible();
    await expect(page.getByRole('tab', { name: removedName, exact: true })).toHaveCount(0);
});

test('a source tab plays only its saved tracks after a disconnected reload', async ({ page, context }) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await setup(page, 3);
    await pinSource(page, 'Empty source');
    await pinSource(page);
    await page.getByRole('tab', { name: sourceName, exact: true }).click();
    const first = panel(page).locator('.home-track').first();
    await clickTrackAction(first, 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect.poll(() => page.evaluate(async bridge => JSON.parse(await (await import(bridge)).offlineTracks(700)).length, bridge)).toBe(1);
    // Remove the synthetic session marker: the fixture client otherwise reconnects
    // even with browser networking disabled, unlike the real TDLib client.
    await page.evaluate(() => new Promise((resolve, reject) => {
        const request = indexedDB.deleteDatabase('/channelwave-telegram/dbfs');
        request.onsuccess = resolve; request.onerror = () => reject(request.error);
    }));
    await context.setOffline(true);
    await page.reload();
    await expect(page.getByRole('tab', { name: 'Empty source', exact: true })).toBeVisible();
    await page.getByRole('tab', { name: sourceName, exact: true }).click();
    await expect(panel(page).locator('.home-track')).toHaveCount(1);
    await expect(panel(page).locator('.home-track-label small')).toHaveText(sourceName);
    await panel(page).getByRole('button', { name: 'Play', exact: true }).click();
    await expect(page.locator('.music-player-status')).toContainText('Playing offline');
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
});

test('failed source reads preserve tabs; a failed pin stays open and can retry', async ({ page }) => {
    await installLoadingGates(page);
    await setup(page, 3);
    await pinSource(page);
    await page.getByRole('button', { name: 'Edit Home tabs', exact: true }).click();
    await page.evaluate(() => fixtureHoldOperation('readSources'));
    await openPicker(page);
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('readSources'))).toBe(1);
    await page.evaluate(() => fixtureReleaseOperation('readSources', true));
    await expect(picker(page).getByRole('alert')).toContainText('Synthetic loading failure');
    await expect(page.locator('.tab-editor-row').last()).toContainText(sourceName);
    await picker(page).getByLabel('Find an item').fill('Empty');
    await expect(picker(page).getByRole('button', { name: 'Empty source', exact: true })).toBeVisible();
    await page.evaluate(() => {
        const put = IDBObjectStore.prototype.put;
        IDBObjectStore.prototype.put = function (value, ...args) {
            if (value.key === 'library-presentation-v1') {
                IDBObjectStore.prototype.put = put;
                throw new DOMException('Synthetic preference storage failure', 'QuotaExceededError');
            }
            return put.call(this, value, ...args);
        };
    });
    await picker(page).getByRole('button', { name: 'Empty source', exact: true }).click();
    await expect(picker(page).getByRole('alert')).toContainText('Synthetic preference storage failure');
    await picker(page).getByRole('button', { name: 'Empty source', exact: true }).click();
    await expect(picker(page)).not.toBeVisible();
    await page.evaluate(() => fixtureHoldOperation('readSources', null, 'after'));
    await page.getByRole('button', { name: 'Add from Library', exact: true }).click();
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('readSources'))).toBe(1);
    await picker(page).getByLabel('Find an item').fill('Night');
    await expect(picker(page).getByRole('button', { name: sourceName, exact: true })).toBeDisabled();
    await page.evaluate(() => fixtureReleaseOperation('readSources', true));
    await expect(picker(page).getByRole('alert')).toHaveCount(0);
    await picker(page).getByLabel('Find an item').fill('Café');
    const source = picker(page).getByRole('button', { name: 'Café Sessions · موسیقی', exact: true });
    await expect(source).toBeVisible();
    await page.evaluate(async bridge => (await import(bridge)).removeSource(700, -100123), bridge);
    await source.click();
    await expect(picker(page).getByRole('alert')).toContainText('This source is no longer in your library.');
});
