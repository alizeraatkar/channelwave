import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { seedPerformance, loaded, nearEnd, viewport, modulePath } from './fixtures/performance.mjs';
import { clickTrackAction, installTelegramFixture, connectFixture, openLibrary } from './fixtures/telegram.mjs';
import { installLoadingGates } from './fixtures/loading.mjs';

test('the usable shell precedes the saved-session probe and cleanup verification stays first', async ({ page }) => {
    await installTelegramFixture(page);
    await installLoadingGates(page, [
        { name: 'readState', query: 'pending-signout-v1', stage: 'before' },
        { name: 'hasSavedTelegramSession', query: null, stage: 'before' }
    ]);
    await page.goto('/');
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('readState'))).toBe(1);
    await expect(page.locator('.startup-screen')).toBeVisible();
    expect(await page.evaluate(() => fixtureOperationPending('hasSavedTelegramSession'))).toBe(0);
    await page.evaluate(() => fixtureReleaseOperation('readState'));
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('hasSavedTelegramSession'))).toBe(1);
    await expect(page.locator('.app-shell')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    await page.evaluate(() => fixtureReleaseOperation('hasSavedTelegramSession'));
});

test('a late page cannot restore erased data or enter the next account', async ({ page }) => {
    await seedPerformance(page, 250);
    await page.evaluate(() => fixtureHoldOperation('searchLibrary', null, 'after', 100));
    await nearEnd(page);
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(1);
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely' })).toBeVisible();
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary'));
    await loaded(page, 0);
    await page.evaluate(async path => { await (await import(path)).saveState(701, 'automatic-metadata-v1', 'false'); }, modulePath);
    await page.addInitScript(() => { globalThis.fixtureAccountId = 701; });
    await page.reload();
    await connectFixture(page);
    await openLibrary(page);
    await loaded(page, 1);
    await expect(page.locator('.music-track-title > strong')).toHaveText(['Other account song']);
    expect(await page.evaluate(() => fixturePageRequests.every(request => request.account === 701))).toBe(true);
});

for (const width of [390, 1440]) {
    test(`10,000-track library appends bounded pages and keeps its window bounded at ${width}px`, async ({ page }, testInfo) => {
        test.setTimeout(180000);
        await page.setViewportSize({ width, height: 900 });
        await seedPerformance(page);
        for (let count = 200; count <= 10000; count += 100) {
            await nearEnd(page);
            await loaded(page, count);
            await expect(page.locator('.track').first()).toBeAttached();
            expect(await page.locator('.track').count()).toBeLessThan(61);
        }
        await nearEnd(page);
        await expect(page.getByRole('button', { name: 'Load more tracks', exact: true })).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Open player for Performance song 10000', exact: true })).toBeVisible();
        const keys = await page.locator('.track').evaluateAll(rows => rows.map(row => row.dataset.trackKey));
        expect(new Set(keys).size).toBe(keys.length);
        await page.locator('#main-content').evaluate(el => { el.scrollTop = 0; });
        await expect(page.getByRole('button', { name: 'Open player for Performance song 1', exact: true })).toBeVisible();
        expect(await page.locator('.track').count()).toBeLessThan(61);
        const requests = await page.evaluate(() => fixturePageRequests.filter(r => r.offset > 0));
        expect(requests.map(r => r.offset)).toEqual(Array.from({ length: 99 }, (_, i) => (i + 1) * 100));
        expect(requests.every(r => r.limit === 101 && r.account === 700)).toBe(true);
        await page.evaluate(() => {
            globalThis.selectionPaintMs = null;
            let started;
            const button = document.querySelector('[aria-label="Select tracks"]');
            button.addEventListener('click', () => { started = performance.now(); }, { once: true });
            const observer = new MutationObserver(() => {
                if (started !== undefined && document.querySelector('.playlist-selection')) {
                    observer.disconnect();
                    requestAnimationFrame(() => requestAnimationFrame(() => { selectionPaintMs = performance.now() - started; }));
                }
            });
            observer.observe(document.querySelector('.music-content'), { childList: true, subtree: true });
        });
        await page.getByRole('button', { name: 'Select tracks', exact: true }).click();
        await expect.poll(() => page.evaluate(() => selectionPaintMs)).not.toBeNull();
        const interaction = await page.evaluate(() => ({ selectionPaintMs, mountedRows: document.querySelectorAll('.track').length }));
        await fs.mkdir('artifacts/performance', { recursive: true });
        await fs.writeFile(`artifacts/performance/long-list-${width}.json`, JSON.stringify(interaction, null, 2));
        await testInfo.attach('long-list-interaction', { body: JSON.stringify(interaction), contentType: 'application/json' });
    });

    test(`failed page retries its offset and stale pages cannot replace search at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await seedPerformance(page, 250);
        await page.evaluate(() => fixtureHoldOperation('searchLibrary', null, 'before', 100));
        await nearEnd(page);
        await expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(1);
        await loaded(page, 100);
        await page.evaluate(() => fixtureReleaseOperation('searchLibrary', true));
        await expect(page.getByRole('button', { name: 'Retry loading music' })).toBeVisible();
        await loaded(page, 100);
        await page.getByRole('button', { name: 'Retry loading music' }).click();
        await loaded(page, 200);
        await page.evaluate(() => fixtureHoldOperation('searchLibrary', null, 'before', 200));
        await nearEnd(page);
        await expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(1);
        await page.locator('#main-content').evaluate(el => { el.scrollTop = 0; });
        await page.getByRole('searchbox', { name: 'Search your indexed library' }).fill('song 249');
        await expect(page.locator('.music-track-title > strong')).toHaveText(['Performance song 249']);
        await page.evaluate(() => fixtureReleaseOperation('searchLibrary'));
        await expect(page.locator('.music-track-title > strong')).toHaveText(['Performance song 249']);
        await loaded(page, 1);
        expect(await page.locator('text=Other account song').count()).toBe(0);
    });

    test(`variable rows, selection, resize and dialog focus preserve browsing at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await seedPerformance(page, 350, { notices: true });
        await page.getByRole('button', { name: 'Select tracks', exact: true }).click();
        await page.getByRole('button', { name: 'Select shown tracks', exact: true }).click();
        await expect(page.getByText('100 selected', { exact: true })).toBeVisible();
        await nearEnd(page); await loaded(page, 200);
        await expect(page.getByText('100 selected', { exact: true })).toBeAttached();
        await nearEnd(page); await loaded(page, 300);
        await nearEnd(page); await loaded(page, 350);
        await nearEnd(page);
        await expect(page.getByRole('button', { name: 'Open player for Performance song 350', exact: true })).toBeVisible();
        const key = '700:00000000-0000-4000-8000-000000000350';
        await page.locator(`.track[data-track-key="${key}"]`).getByRole('button', { name: 'More actions', exact: true }).click();
        await page.locator('#main-content').evaluate(el => { el.scrollTop = 0; });
        await expect(page.locator(`.track[data-track-key="${key}"]`)).toHaveCount(1);
        await page.getByRole('button', { name: 'Close song actions', exact: true }).click();
        await expect(page.locator(`.track[data-track-key="${key}"]`).getByRole('button', { name: 'More actions', exact: true })).toBeFocused();
        await page.setViewportSize({ width: width === 390 ? 1440 : 390, height: 900 });
        expect(await page.locator('.track').count()).toBeLessThan(62);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await fs.mkdir('artifacts/performance', { recursive: true });
        await page.screenshot({ path: `artifacts/performance/variable-rows-${page.viewportSize().width}.png` });
    });

    test(`playback position updates leave the list render revision unchanged at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await seedPerformance(page, 200);
        await clickTrackAction(page.locator('.track').first(), 'Stream');
        const dock = page.getByRole('region', { name: 'Now playing', exact: true });
        await expect(dock.getByRole('button', { name: 'Pause playback', exact: true })).toBeEnabled();
        await expect.poll(() => dock.getByRole('slider', { name: 'Playback position' }).inputValue()).not.toBe('0');
        const revision = await viewport(page).getAttribute('data-render-revision');
        const position = await dock.getByRole('slider', { name: 'Playback position' }).inputValue();
        await expect.poll(() => dock.getByRole('slider', { name: 'Playback position' }).inputValue()).not.toBe(position);
        expect(await viewport(page).getAttribute('data-render-revision')).toBe(revision);
    });
}
