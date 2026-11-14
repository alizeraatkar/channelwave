import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { seed } from './fixtures/redesign.mjs';
import { openRoot, installTelegramFixture, connectFixture, openChannels } from './fixtures/telegram.mjs';
import { installLoadingGates } from './fixtures/loading.mjs';

const baseline = process.env.CHANNELWAVE_HOME_BASELINE === '1';
const panel = page => page.locator('#home-tab-content');
const paint = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
const bridge = '/_content/ChannelWave.Platform.Browser/channelwave.js';

test('connection status takes priority over indexing without moving the Home tabs', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    await page.evaluate(() => { globalThis.fixtureHoldIndex = true; });
    await page.getByRole('button', { name: 'Add to library: :)', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof globalThis.releaseFixtureIndex)).toBe('function');
    await openRoot(page, 'Home');
    await expect(page.locator('.telegram-activity')).toHaveText('Indexing…');
    await expect(page.locator('.app-topbar .brand strong')).toHaveText('ChannelWave');
    const top = (await page.locator('.home-tab-strip').boundingBox()).y;
    await page.evaluate(() => fixtureClient.options.onUpdate({ '@type': 'updateConnectionState', state: { '@type': 'connectionStateConnecting' } }));
    await expect(page.locator('.telegram-activity')).toHaveText('Connecting…');
    expect((await page.locator('.home-tab-strip').boundingBox()).y).toBe(top);
    await page.evaluate(() => fixtureClient.options.onUpdate({ '@type': 'updateConnectionState', state: { '@type': 'connectionStateReady' } }));
    await expect(page.locator('.telegram-activity')).toHaveText('Indexing…');
    await page.evaluate(() => { fixtureHoldIndex = false; for (const release of releaseFixtureIndexes) release(); });
    await expect(page.locator('.app-topbar .brand strong')).toHaveText('ChannelWave', { timeout: 15000 });
    await expect(page.locator('.telegram-activity')).toBeEmpty({ timeout: 15000 });
    expect((await page.locator('.home-tab-strip').boundingBox()).y).toBe(top);
});

test('warm Home navigation and tabs reuse a 10,000-track collection', async ({ page }, info) => {
    test.setTimeout(180000);
    await page.setViewportSize({ width: 390, height: 844 });
    await installLoadingGates(page);
    await seed(page, 10000);
    await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
    await paint(page);
    const samples = [];
    for (let i = 0; i < 5; i++) {
        await page.getByRole('tab', { name: 'Liked', exact: true }).click();
        await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
        await paint(page);
        const before = await page.evaluate(() => fixturePageRequests.length);
        const started = Date.now();
        await page.getByRole('tab', { name: 'New', exact: true }).click();
        await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
        await paint(page);
        samples.push({ ms: Date.now() - started, reads: await page.evaluate(() => fixturePageRequests.length) - before });
    }
    const before = await page.evaluate(() => fixturePageRequests.length);
    await openRoot(page, 'Settings');
    await openRoot(page, 'Home');
    await expect(panel(page).locator('.home-track').first()).toBeVisible();
    await paint(page);
    const selected = await page.getByRole('tab', { selected: true }).innerText();
    const result = { samples, returnReads: await page.evaluate(() => fixturePageRequests.length) - before, selected };
    await fs.mkdir('artifacts/home-session', { recursive: true });
    await fs.writeFile(`artifacts/home-session/${baseline ? 'before' : 'after'}.json`, JSON.stringify(result, null, 2));
    await info.attach('home-performance', { body: JSON.stringify(result), contentType: 'application/json' });
    await page.screenshot({ path: `artifacts/home-session/${baseline ? 'before' : 'after'}.png` });
    if (!baseline) {
        expect(samples.every(sample => sample.reads === 0)).toBe(true);
        expect(result.returnReads).toBe(0);
        expect(selected).toBe('New');
    }
});

test('Home playback stays active while moving between roots', async ({ page }) => {
    await seed(page);
    await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await panel(page).locator('.home-track').first().getByRole('button', { name: 'Play', exact: true }).click();
    try { await expect(page.locator('.music-player-status')).toContainText('Streaming', { timeout: 15000 }); }
    catch (error) {
        console.log(await page.evaluate(async path => ({ calls: fixtureCalls, downloads: globalThis.fixtureDownloads,
            audio: await (await import(path)).audioState(), worker: navigator.serviceWorker.controller?.scriptURL,
            resources: performance.getEntriesByType('resource').map(entry => entry.name).filter(name => name.includes('audio') || name.includes('worker')) } ), bridge));
        throw error;
    }
    await openRoot(page, 'Settings');
    await openRoot(page, 'Home');
    await expect(page.locator('.music-player-status')).toContainText('Streaming');
    expect(await page.evaluate(() => fixtureAudio.paused)).toBe(false);
});

test('editing metadata patches Home rows without searching the hidden Library', async ({ page }) => {
    await installLoadingGates(page);
    await seed(page);
    await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    const row = panel(page).locator('.home-track').first();
    await row.getByRole('button', { name: 'More actions', exact: true }).click();
    await page.getByRole('dialog', { name: 'Song actions', exact: true }).getByRole('button', { name: 'Edit details', exact: true }).click();
    const editor = page.getByRole('dialog', { name: 'Edit details', exact: true });
    await editor.getByLabel('Local title', { exact: true }).fill('عنوان اصلاح‌شده');
    const reads = await page.evaluate(() => fixturePageRequests.length);
    await editor.getByRole('button', { name: 'Save local corrections', exact: true }).click();
    await expect(editor.getByRole('status')).toContainText('Local corrections saved');
    await editor.getByRole('button', { name: 'Close details', exact: true }).click();
    await expect(row.locator('.music-title-link')).toHaveText('عنوان اصلاح‌شده');
    expect(await page.evaluate(() => fixturePageRequests.length)).toBe(reads);
});

for (const width of [320, 390, 760, 1440]) {
    test(`Home rows and status changes keep their geometry at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await installLoadingGates(page);
        await seed(page, 250);
        await page.evaluate(async path => {
            const lib = await import(path);
            const track = discoveryTracks[0];
            await lib.setLocalMetadata(700, track.id, 'ترانهٔ بسیار طولانی برای آزمون نمایش عنوان و نام خواننده در موبایل', 'هنرمند با نام طولانی برای آزمون چیدمان');
        }, bridge);
        await openRoot(page, 'Home');
        await page.getByRole('tab', { name: 'New', exact: true }).click();
        await page.getByRole('button', { name: 'List view', exact: true }).click();
        await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
        await paint(page);
        const geometry = await panel(page).locator('.home-track').first().evaluate(row => {
            const box = el => { const r = el.getBoundingClientRect(); return { x: r.x, right: r.right, width: r.width, height: r.height }; };
            return { row: box(row), art: box(row.querySelector('.music-artwork-open')), image: box(row.querySelector('.music-art')), label: box(row.querySelector('.home-track-label')), actions: box(row.querySelector('.music-track-actions')) };
        });
        expect(geometry.row.height).toBeLessThan(140);
        expect(geometry.image.width).toBeGreaterThanOrEqual(48);
        expect(geometry.image.height).toBeGreaterThanOrEqual(48);
        expect(geometry.art.right).toBeLessThanOrEqual(geometry.label.x);
        expect(geometry.label.right).toBeLessThanOrEqual(geometry.actions.x);
        expect(geometry.label.width).toBeGreaterThan(70);
        expect(geometry.actions.right).toBeLessThanOrEqual(geometry.row.right + 1);
        if (width <= 760) await expect(panel(page).locator('.track-favorite').first()).toBeHidden();
        const top = (await page.locator('.home-tab-strip').boundingBox()).y;
        const viewport = panel(page).locator('.virtual-tracks');
        const renders = await viewport.getAttribute('data-render-revision');
        const reads = await page.evaluate(() => fixturePageRequests.length);
        for (const state of ['connectionStateConnecting', 'connectionStateWaitingForNetwork', 'connectionStateUpdating', 'connectionStateReady']) {
            await page.evaluate(state => fixtureClient.options.onUpdate({ '@type': 'updateConnectionState', state: { '@type': state } }), state);
            if (state === 'connectionStateReady') await expect(page.locator('.telegram-activity')).toBeEmpty();
            else await expect(page.locator('.telegram-activity')).not.toBeEmpty();
            await expect(page.locator('.app-topbar .brand strong')).toHaveText('ChannelWave');
            expect((await page.locator('.home-tab-strip').boundingBox()).y).toBe(top);
        }
        expect(await page.evaluate(() => fixturePageRequests.length)).toBe(reads);
        expect(await viewport.getAttribute('data-render-revision')).toBe(renders);
        await fs.mkdir('artifacts/home-session', { recursive: true });
        await page.screenshot({ path: `artifacts/home-session/list-${width}.png` });
        await page.getByRole('button', { name: 'Grid view', exact: true }).click();
        await paint(page);
        const grid = await panel(page).locator('.home-track').first().boundingBox();
        expect(grid.width).toBeGreaterThan(100);
        expect(grid.x + grid.width).toBeLessThanOrEqual(width);
        await page.screenshot({ path: `artifacts/home-session/grid-${width}.png` });
    });
}

test('Home restores each tab scroll across roots and visibility changes; cold reload starts at All', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await installLoadingGates(page);
    await seed(page, 500);
    await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
    await page.locator('#main-content').evaluate(el => el.scrollTop = 1500);
    await paint(page);
    const top = await page.locator('#main-content').evaluate(el => el.scrollTop);
    await openRoot(page, 'Settings');
    await openRoot(page, 'Home');
    await expect(page.getByRole('tab', { name: 'New', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect.poll(() => page.locator('#main-content').evaluate(el => el.scrollTop)).toBeGreaterThan(top - 80);
    await page.getByRole('tab', { name: 'Liked', exact: true }).click();
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await expect.poll(() => page.locator('#main-content').evaluate(el => el.scrollTop)).toBeGreaterThan(top - 80);
    const before = await page.evaluate(() => fixturePageRequests.length);
    await page.evaluate(() => {
        Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
        document.dispatchEvent(new Event('visibilitychange'));
    });
    await paint(page);
    await page.evaluate(() => {
        Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
        document.dispatchEvent(new Event('visibilitychange'));
    });
    await paint(page);
    expect(await page.evaluate(() => fixturePageRequests.length)).toBe(before);
    await page.evaluate(() => new Promise(resolve => {
        const request = indexedDB.open('/channelwave-telegram/dbfs', 1);
        request.onsuccess = () => { request.result.close(); resolve(); };
    }));
    await page.reload();
    await expect(page.getByRole('tab', { name: 'All', exact: true })).toHaveAttribute('aria-selected', 'true');
    expect(await page.evaluate(() => globalThis.fixtureAudio?.paused ?? true)).toBe(true);
});

test('slow Home reads survive tab switches, retry a failed page, and cannot return after sign-out', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await installLoadingGates(page);
    await seed(page, 250);
    await openRoot(page, 'Home');
    await page.evaluate(() => fixtureHoldOperation('searchLibrary', '', 'after', 0));
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(1);
    await page.getByRole('tab', { name: 'Liked', exact: true }).click();
    await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary'));
    await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    await page.evaluate(() => fixtureHoldOperation('searchLibrary', '', 'before', 100));
    await page.locator('#main-content').evaluate(el => el.scrollTop = el.scrollHeight);
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(1);
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary', true));
    await expect(panel(page).getByRole('button', { name: 'Retry', exact: true })).toBeVisible();
    await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
    await panel(page).getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '200');
    await page.evaluate(() => fixtureHoldOperation('searchLibrary', '', 'after', 200));
    await page.locator('#main-content').evaluate(el => el.scrollTop = el.scrollHeight);
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(1);
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely' })).toBeVisible();
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary'));
    await openRoot(page, 'Home');
    await expect(page.locator('.home-track')).toHaveCount(0);
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).searchLibrary(700, '', 0, 10)).length, bridge)).toBe(0);
});
