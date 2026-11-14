import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, addFixtureSource, openLibrary, openDownloads, clickTrackAction } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
async function setup(page) {
    await installTelegramFixture(page);
    await page.goto('/'); await connectFixture(page); await addFixtureSource(page); await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(1);
}
async function startSave(page, action = 'Save offline') {
    await clickTrackAction(page.locator('.track'), action);
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
}
async function downloads(page) { await openDownloads(page); }

for (const width of [1440, 390]) {
    test(`downloads remain manageable across views and offline songs survive at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
        await setup(page); await startSave(page); await downloads(page);
        await expect(page.locator('.download-row')).toHaveCount(1);
        await expect(page.locator('.download-row')).toContainText('Saving offline');
        await fs.mkdir('artifacts/offline-milestone', { recursive: true });
        await page.screenshot({ path: `artifacts/offline-milestone/downloads-${width}.png` });
        await page.evaluate(() => completeFixtureDownload());
        await page.getByRole('button', { name: /^Completed/ }).click();
        await expect(page.locator('.download-row')).toContainText('Saved offline');
        await openLibrary(page);
        await page.locator('.music-tabs').getByRole('button', { name: 'Offline', exact: true }).click();
        await expect(page.locator('.track')).toHaveCount(1);
        await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'false');
        await expect(page.locator('.music-tabs').getByRole('button', { name: 'Offline', exact: true })).toHaveCSS('color', 'rgb(0, 0, 0)');
        await page.screenshot({ path: `artifacts/offline-milestone/offline-${width}.png` });
        await openRoot(page, 'Settings');
        await page.getByRole('button', { name: 'Manage storage', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Storage', exact: true })).toBeVisible();
        await expect(page.getByRole('button', { name: '256 MB', exact: true })).toHaveAttribute('aria-pressed', 'true');
        await page.getByRole('button', { name: '512 MB', exact: true }).click();
        await expect(page.getByRole('button', { name: '512 MB', exact: true })).toHaveAttribute('aria-pressed', 'true');
        await expect(page.getByRole('button', { name: '512 MB', exact: true })).toBeEnabled();
        await page.getByRole('button', { name: '512 MB', exact: true }).hover();
        await expect(page.getByRole('button', { name: '512 MB', exact: true })).toHaveCSS('color', 'rgb(0, 0, 0)');
        await expect(page.getByRole('button', { name: '512 MB', exact: true })).toHaveCSS('background-color', 'rgb(255, 255, 255)');
        await page.screenshot({ path: `artifacts/offline-milestone/storage-${width}.png` });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.getByRole('button', { name: 'Clear temporary cache', exact: true }).click();
        await page.getByRole('dialog', { name: 'Clear temporary cache?' }).getByRole('button', { name: 'Clear cache', exact: true }).click();
        await expect(page.getByRole('status').filter({ hasText: 'Eligible temporary files cleared' })).toBeVisible();
        expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)).length, modulePath)).toBe(1);
        await page.getByRole('button', { name: /Manage saved songs/ }).click();
        await expect(page.locator('.track')).toHaveCount(1);
        await expect(page.locator('#blazor-error-ui')).toBeHidden();
    });
}

test('an interrupted save recovers after reconnect and cancellation stays canceled after reload', async ({ page }) => {
    await setup(page); await startSave(page); await downloads(page); await page.reload();
    await openRoot(page, 'Profile'); await connectFixture(page);
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
    await downloads(page);
    await page.getByRole('button', { name: 'Cancel Café 🎵', exact: true }).click();
    await page.getByRole('button', { name: /^Completed/ }).click();
    await expect(page.locator('.download-row')).toContainText('Canceled');
    await page.reload(); await openRoot(page, 'Profile'); await connectFixture(page); await downloads(page);
    await expect(page.locator('.download-row')).toHaveCount(0);
    await page.getByRole('button', { name: /^Completed/ }).click();
    await expect(page.locator('.download-row')).toContainText('Canceled');
});

test('an export waits for an explicit save and is not automatically repeated after reload', async ({ page }) => {
    await setup(page); await startSave(page, 'Download'); await page.evaluate(() => completeFixtureDownload()); await downloads(page);
    await expect(page.getByRole('button', { name: 'Save file', exact: true })).toBeVisible();
    const before = await page.evaluate(async path => JSON.parse(await (await import(path)).readState(700, 'transfers:v1')), modulePath);
    expect(before.Jobs[0].Purpose).toBe(1);
    await page.reload(); await openRoot(page, 'Profile'); await connectFixture(page); await downloads(page);
    await expect(page.getByRole('button', { name: 'Save file', exact: true })).toBeVisible();
    // The synthetic Telegram engine has no persisted file cache, so complete preparation again on explicit export.
    const saved = page.waitForEvent('download'); await page.getByRole('button', { name: 'Save file', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function'); await page.evaluate(() => completeFixtureDownload());
    await saved;
    await page.getByRole('button', { name: /^Completed/ }).click(); await expect(page.locator('.download-row')).toContainText('Sent to your device');
});

test('saving a playlist snapshots its songs and keeps them after the playlist is deleted', async ({ page }) => {
    await setup(page);
    await page.locator('.music-tabs').getByRole('button', { name: /^Listening list/ }).click();
    const manager = page.getByRole('region', { name: 'Playlist management' });
    await manager.getByRole('button', { name: 'New playlist', exact: true }).click();
    await manager.getByLabel('New playlist name').fill('Offline trip');
    await manager.getByRole('button', { name: 'Save playlist', exact: true }).click();
    await page.locator('.music-tabs').getByRole('button', { name: 'All tracks', exact: true }).click();
    await clickTrackAction(page.locator('.track'), 'Add to another playlist');
    await page.getByRole('dialog', { name: 'Add to playlist', exact: true }).getByRole('button', { name: /Offline trip/ }).click();
    await page.locator('.music-tabs').getByRole('button', { name: /^Listening list/ }).click();
    await expect(page.getByRole('heading', { name: 'Offline trip', exact: true })).toBeVisible();
    await page.locator('#music-search').fill('no matching songs');
    await expect(page.locator('.track')).toHaveCount(0);
    await page.getByRole('button', { name: 'Save current songs offline', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    await expect.poll(() => page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)).length, modulePath)).toBe(1);
    const deleteButton = manager.getByRole('button', { name: 'Delete playlist', exact: true });
    if (!await deleteButton.isVisible()) await manager.getByRole('button', { name: 'Playlist actions', exact: true }).click();
    await deleteButton.click(); await manager.getByRole('button', { name: 'Delete', exact: true }).click();
    await page.locator('#music-search').fill('');
    await page.locator('.music-tabs').getByRole('button', { name: 'Offline', exact: true }).click();
    await expect(page.locator('.track')).toHaveCount(1);
});

test('download filters and storage controls remain usable at 320px with doubled text', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 }); await setup(page);
    await page.addStyleTag({ content: '.media-page { font-size:28px } .media-page button,.media-page p,.media-page dt,.media-page dd { font-size:28px!important } .media-page h1 { font-size:56px!important } .media-page h2 { font-size:36px!important }' }); await downloads(page);
    for (const name of ['In progress', 'Completed', 'Needs attention']) {
        const button = page.getByRole('button', { name: new RegExp('^' + name) });
        await expect(button).toBeInViewport(); await button.focus(); await page.keyboard.press('Enter');
        await expect(button).toHaveAttribute('aria-pressed', 'true');
    }
    await openRoot(page, 'Settings'); await page.getByRole('button', { name: 'Manage storage', exact: true }).click();
    await expect(page.getByRole('button', { name: '256 MB', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await fs.mkdir('artifacts/offline-milestone', { recursive: true });
    await page.screenshot({ path: 'artifacts/offline-milestone/storage-320-text-200.png' });
});
