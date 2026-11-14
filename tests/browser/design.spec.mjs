import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, openRoot, openLibrary, openChannels, connectFixture, addFixtureSource, clickTrackAction, stopPlayback } from './fixtures/telegram.mjs';

for (const width of [1440, 390]) {
    test(`complete music navigation preserves playback at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
        await installTelegramFixture(page, 'sync');
        await page.goto('/');
        await expect(page.getByRole('heading', { name: /Your sources.*Your kind of music/ })).toBeVisible({ timeout: 15000 });
        await fs.mkdir('artifacts/browser-review', { recursive: true });
        const capture = async name => {
            await page.evaluate(() => window.scrollTo(0, 0));
            expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
            await page.screenshot({ path: `artifacts/browser-review/${name}-${width}.png` });
        };
        await capture('home');
        await openRoot(page, 'Profile');
        await expect(page.getByRole('heading', { name: 'Connect Telegram', exact: true })).toBeVisible();
        await capture('connect');
        await connectFixture(page);
        await expect(page.getByRole('region', { name: 'Choose a source' }).locator('.channel-row')).toHaveCount(4);
        for (const name of [':)', 'Café Sessions', 'آهنگ‌های من 🎵']) {
            await addFixtureSource(page, name);
        }
        await capture('channels');
        await openLibrary(page);
        const library = page.getByRole('region', { name: 'Local library', exact: true });
        await expect(library.locator('.track')).toHaveCount(3);
        await clickTrackAction(library.locator('.track').first(), 'Favorite track');
        await clickTrackAction(library.locator('.track').first(), 'Add to playlist');
        await library.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true }).click();
        await expect(library.locator('.track')).toHaveCount(1);
        await expect(library.locator('.music-feedback')).toHaveCount(0);
        await capture('liked');
        await library.locator('.music-tabs').getByRole('button', { name: /Listening list 1/ }).click();
        await expect(library.locator('.track')).toHaveCount(1);
        await capture('playlist');
        await library.locator('.music-tabs').getByRole('button', { name: 'All tracks', exact: true }).click();
        await library.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        const player = page.getByRole('region', { name: 'Now playing', exact: true });
        await expect(player.getByRole('status')).toContainText('Streaming from Telegram');
        await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
        if (width === 390) {
            for (const button of await player.getByRole('button').all()) {
                const bounds = await button.boundingBox();
                expect(bounds.width).toBeGreaterThanOrEqual(44);
                expect(bounds.height).toBeGreaterThanOrEqual(44);
            }
            const primaryAction = await library.locator('.track').first().getByRole('button', { name: 'Resume', exact: true }).boundingBox();
            const playerBounds = await player.boundingBox();
            expect(primaryAction.y + primaryAction.height).toBeLessThanOrEqual(playerBounds.y);
        }
        await capture('library');
        await openChannels(page);
        await expect(player).toBeVisible();
        await player.getByRole('button', { name: 'Resume playback', exact: true }).click();
        expect(await page.evaluate(() => fixtureAudio.paused)).toBe(false);
        await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
        await openRoot(page, 'Library');
        await page.getByRole('button', { name: 'Open search', exact: true }).click();
        await library.getByRole('searchbox').fill('no such song');
        await library.getByRole('button', { name: 'Search', exact: true }).click();
        await expect(library.getByRole('heading', { name: 'No matches just yet' })).toBeVisible();
        await capture('search');
        await openLibrary(page);
        await expect(library.locator('.track')).toHaveCount(3);
        await expect(player).toBeVisible();
        await openRoot(page, 'Profile');
        await capture('account');
        await stopPlayback(page);
        await expect(player).toHaveCount(0);
        await expect(page.getByRole('alert')).toHaveCount(0);
    });
}

test('mobile connection follows phone, code and password prompts', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await installTelegramFixture(page, 'auth');
    await page.goto('/');
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await page.getByLabel('Phone number with country code', { exact: true }).fill('+15550100000');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await page.getByLabel('Verification code', { exact: true }).fill('00000');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await expect(page.getByLabel('Two-step verification password', { exact: true })).toHaveAttribute('type', 'password');
    await fs.mkdir('artifacts/browser-review', { recursive: true });
    await page.screenshot({ path: 'artifacts/browser-review/auth-390.png' });
    await page.getByLabel('Two-step verification password', { exact: true }).fill('synthetic-password');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Choose a source' })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
});
