import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { expectLoadedTracks } from './fixtures/library.mjs';
import { installTelegramFixture, openLibrary, openChannels, clickTrackAction, connectFixture, addFixtureSource, savePreparedExport } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const transfer = page => page.getByRole('region', { name: 'Active transfer', exact: true });

async function setup(page) {
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    for (const name of [':)', 'Café Sessions']) {
        await addFixtureSource(page, name);
        await expect(page.getByRole('button', { name: `Remove from library: ${name}`, exact: true })).toBeEnabled();
    }
    await openLibrary(page);
    await expectLoadedTracks(page, 2);
    await page.locator('#main-content').evaluate(el => el.scrollTop = el.scrollHeight);
    await expect(page.locator('.track')).toHaveCount(2);
    await page.evaluate(() => {
        const send = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            if (query['@type'] === 'downloadFile' && !query.synchronous) globalThis.transferClient = this;
            const response = await send.call(this, query);
            return query['@type'] === 'downloadFile' && !query.synchronous
                ? { ...response, local: { ...response.local, downloaded_size: 1000 } } : response;
        };
    });
}

async function checkClearance(page) {
    const panel = await transfer(page).boundingBox();
    const player = await page.locator('.music-player, .idle-player:visible').first().boundingBox();
    expect(panel.y).toBeGreaterThanOrEqual(0);
    expect(panel.x).toBeGreaterThanOrEqual(0);
    expect(panel.x + panel.width).toBeLessThanOrEqual(page.viewportSize().width);
    expect(panel.y + panel.height).toBeLessThanOrEqual(player.y);
    const cancel = transfer(page).getByRole('button', { name: 'Cancel download', exact: true });
    const bounds = await cancel.boundingBox();
    expect(bounds.height).toBeGreaterThanOrEqual(44);
    expect(await cancel.evaluate(element => {
        const rect = element.getBoundingClientRect();
        return element.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2));
    })).toBe(true);
}

for (const viewport of [{ width: 1440, height: 900 }, { width: 320, height: 568 }, { width: 844, height: 390 }]) {
    for (const action of ['Download', 'Save offline']) {
        test(`${action} keeps progress and cancellation across views at ${viewport.width}px (CW-QA-007)`, async ({ page }) => {
            await page.setViewportSize(viewport);
            await setup(page);
            await clickTrackAction(page.locator('.track').first(), action);
            await expect(transfer(page)).toBeVisible();
            await expect(transfer(page).getByRole('progressbar')).toHaveAttribute('value', '1000');
            await openRoot(page, 'Home');
            await expect(transfer(page)).toBeVisible();
            await page.evaluate(() => transferClient.options.onUpdate({
                '@type': 'updateFile', file: { '@type': 'file', id: 1, size: 80044, expected_size: 80044,
                    local: { is_downloading_completed: false, is_downloading_active: true, downloaded_size: 40000 },
                    remote: { id: 'fixture', unique_id: 'fixture-unique' } }
            }));
            await expect(transfer(page).getByRole('progressbar')).toHaveAttribute('value', '40000');
            await expect(transfer(page).getByRole('progressbar')).toHaveAttribute('max', '80044');
            await expect(transfer(page).getByRole('status')).toContainText('39.1 KB of 78.2 KB');
            await checkClearance(page);
            await page.screenshot({ path: `artifacts/qa-fixes/transfer-${viewport.width}-${action.replace(' ', '-')}.png` });

            await openRoot(page, 'Library');
            await page.getByRole('button', { name: 'Open search', exact: true }).click();
            await page.getByRole('searchbox', { name: 'Search your indexed library' }).fill('no matching song');
            await page.locator('.music-search').getByRole('button', { name: 'Search', exact: true }).click();
            await expect(page.locator('.track')).toHaveCount(0);
            await expect(transfer(page)).toBeVisible();
            for (const name of ['Offline', 'Liked Songs']) {
                await page.locator('.music-tabs').getByRole('button', { name, exact: true }).click();
                await expect(transfer(page)).toBeVisible();
            }
            await page.locator('.music-tabs').getByRole('button', { name: /Listening list/ }).click();
            await expect(transfer(page)).toBeVisible();
            await openChannels(page);
            await expect(transfer(page)).toBeVisible();
            await openRoot(page, 'Profile');
            await expect(transfer(page)).toBeVisible();
            await checkClearance(page);
            await transfer(page).getByRole('button', { name: 'Cancel download', exact: true }).click();
            await expect(transfer(page)).toHaveCount(0);
            expect(await page.evaluate(() => fixtureCalls.includes('cancelDownloadFile'))).toBe(true);
            expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
            await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
        });
    }
}

for (const action of ['Download', 'Save offline']) {
    test(`${action} finishes from Home and clears the persistent transfer panel (CW-QA-007)`, async ({ page }) => {
        await setup(page);
        let exported = false;
        page.on('download', () => { exported = true; });
        await clickTrackAction(page.locator('.track').first(), action);
        await expect(transfer(page)).toBeVisible();
        await openRoot(page, 'Home');
        await page.evaluate(() => completeFixtureDownload());
        await expect(transfer(page)).toHaveCount(0);
        if (action === 'Download') { await savePreparedExport(page); await expect.poll(() => exported).toBe(true); }
        else {
            expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toHaveLength(1);
            await openLibrary(page);
            await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
        }
    });
}

for (const viewport of [{ width: 320, height: 568 }, { width: 844, height: 390 }]) {
    test(`transfer, player and keyboard focus share space at ${viewport.width}px (CW-QA-007)`, async ({ page }) => {
        await page.setViewportSize(viewport);
        await setup(page);
        await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
        await clickTrackAction(page.locator('.track').last(), 'Download');
        await expect(transfer(page)).toBeVisible();
        if (viewport.width === 320) {
            await page.evaluate(() => {
                for (const element of document.querySelectorAll('.music-transfer strong, .music-transfer span, .music-transfer button')) {
                    element.style.fontSize = `${parseFloat(getComputedStyle(element).fontSize) * 2}px`;
                }
            });
        }
        await checkClearance(page);
        await page.locator('#music-search').focus();
        await page.keyboard.press('Tab');
        await expect.poll(() => page.evaluate(() => {
            const rect = document.activeElement.getBoundingClientRect();
            const panel = document.querySelector('.music-transfer').getBoundingClientRect();
            return rect.top >= 0 && rect.bottom < panel.top;
        })).toBe(true);
        await page.screenshot({ path: `artifacts/qa-fixes/transfer-player-${viewport.width}.png` });
        await transfer(page).getByRole('button', { name: 'Cancel download', exact: true }).click();
        await expect(transfer(page)).toHaveCount(0);
        await expect(page.getByRole('region', { name: 'Now playing' })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
    });
}
