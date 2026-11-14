import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture, connectFixture, addFixtureSource, openLibrary, openChannels, clickTrackAction } from './fixtures/telegram.mjs';

for (const action of ['cancel', 'sign out', 'remove source']) {
    test(`explicit export final read cannot create a copy after ${action}`, async ({ page }) => {
        await installTelegramFixture(page); await page.goto('/'); await connectFixture(page); await addFixtureSource(page); await openLibrary(page);
        await clickTrackAction(page, 'Download'); await page.evaluate(() => completeFixtureDownload());
        await page.getByRole('button', { name: 'Open Downloads', exact: true }).click();
        await expect(page.getByRole('button', { name: 'Save file', exact: true })).toBeVisible();
        await page.evaluate(() => {
            const send = tdweb.default.prototype.send;
            tdweb.default.prototype.send = async function (query) {
                const response = await send.call(this, query);
                if (query['@type'] === 'readFile') await new Promise(resolve => { globalThis.releaseExportRead = resolve; });
                return response;
            };
            const click = HTMLAnchorElement.prototype.click; globalThis.exportHandoffs = 0;
            HTMLAnchorElement.prototype.click = function () { if (this.download) ++exportHandoffs; return click.call(this); };
            globalThis.exportDownloadsBefore = fixtureDownloads.length;
        });
        await page.getByRole('button', { name: 'Save file', exact: true }).click();
        await expect.poll(() => page.evaluate(() => fixtureDownloads.length > exportDownloadsBefore)).toBe(true);
        await page.evaluate(() => completeFixtureDownload());
        await expect.poll(() => page.evaluate(() => typeof releaseExportRead)).toBe('function');
        if (action === 'cancel') {
            const cancel = page.getByRole('button', { name: 'Cancel Café 🎵', exact: true });
            await expect(cancel).toBeEnabled(); await cancel.click();
            await page.getByRole('button', { name: /^Completed/ }).click();
            await expect(page.locator('.download-row')).toContainText('Canceled');
        } else if (action === 'sign out') {
            await openRoot(page, 'Profile');
            await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
            await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely' })).toBeVisible();
        } else {
            await openChannels(page); await page.getByRole('button', { name: 'Remove from library: :)', exact: true }).click();
            await expect(page.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeVisible();
        }
        await page.evaluate(async () => { releaseExportRead(); await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); });
        expect(await page.evaluate(() => exportHandoffs)).toBe(0);
        await expect(page.locator('#blazor-error-ui')).toBeHidden();
    });
}
