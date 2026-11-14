import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, openLibrary, openChannels, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

async function capture(page, name) {
    await fs.mkdir('artifacts/browser-review', { recursive: true });
    await page.screenshot({ path: `artifacts/browser-review/visual-fix-${name}.png` });
}

async function setup(page) {
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    await addFixtureSource(page);
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(1);
}

test('desktop track actions stay on one line with saved and playing states', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await setup(page);
    const row = page.locator('.track').first();
    const checkActions = async () => {
        const boxes = await row.locator('.music-track-actions button').evaluateAll(buttons =>
            buttons.map(button => ({ top: button.getBoundingClientRect().top, bottom: button.getBoundingClientRect().bottom })));
        expect(Math.max(...boxes.map(box => box.top))).toBeLessThan(Math.min(...boxes.map(box => box.bottom)));
    };
    await checkActions();
    await capture(page, 'desktop-actions');
    await clickTrackAction(row, 'Save offline');
    await page.evaluate(() => completeFixtureDownload());
    await expect(row.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    await checkActions();
    await row.getByRole('button', { name: 'Play', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Now playing' }).getByRole('status')).toContainText('Playing offline');
    await checkActions();
});

for (const width of [320, 390]) {
    test(`phone player gives song identity room at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 844 });
        await setup(page);
        await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        const player = page.getByRole('region', { name: 'Now playing' });
        await expect(player.getByRole('status')).toContainText('Streaming');
        const title = await player.locator('.music-player-info strong').boundingBox();
        expect(title.width).toBeGreaterThanOrEqual(70);
        const info = await player.locator('.music-player-info').boundingBox();
        const controls = await player.locator('.music-player-toggle').boundingBox();
        expect(info.x + info.width).toBeLessThanOrEqual(controls.x);
        for (const button of await player.getByRole('button').all()) {
            const box = await button.boundingBox();
            expect(box.width).toBeGreaterThanOrEqual(44);
            expect(box.height).toBeGreaterThanOrEqual(44);
        }
        await capture(page, `player-${width}`);
    });

    test(`enlarged mobile header and idle player fit at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 844 });
        await installTelegramFixture(page);
        await page.goto('/');
        await expect(page.getByRole('heading', { name: /Your sources.*Your kind of music/ })).toBeVisible();
        await page.evaluate(() => {
            const elements = [...document.querySelectorAll('.app-topbar strong, .idle-player strong, .idle-player span')];
            const sizes = elements.map(el => parseFloat(getComputedStyle(el).fontSize) * 2);
            elements.forEach((el, i) => { el.style.fontSize = `${sizes[i]}px`; });
        });
        const settings = await page.getByRole('button', { name: 'Settings', exact: true }).boundingBox();
        const account = await page.getByRole('button', { name: 'Profile', exact: true }).boundingBox();
        expect(settings.x + settings.width).toBeLessThanOrEqual(account.x + 1);
        expect(account.x + account.width).toBeLessThanOrEqual(width);
        const idle = page.locator('.idle-player');
        const bounds = await idle.boundingBox();
        for (const text of await idle.locator('strong, span').all()) {
            const box = await text.boundingBox();
            expect(box.x).toBeGreaterThanOrEqual(bounds.x);
            expect(box.x + box.width).toBeLessThanOrEqual(bounds.x + bounds.width);
            expect(box.y).toBeGreaterThanOrEqual(bounds.y);
            expect(box.y + box.height).toBeLessThanOrEqual(bounds.y + bounds.height);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await capture(page, `enlarged-${width}`);
    });
}

for (const width of [820, 1600]) {
    test(`compact collection heading aligns with the content gutter at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 });
        await setup(page);
        await page.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true }).click();
        const gutter = await page.locator('.music-search').boundingBox();
        const banner = await page.locator('.favorites-heading').boundingBox();
        expect(banner.x).toBeCloseTo(gutter.x, 0);
        expect(banner.width).toBeLessThanOrEqual(width - banner.x);
        await capture(page, `banner-${width}`);
    });
}

test('phone metadata fields remain readable without small input text', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 844 });
    await setup(page);
    await clickTrackAction(page.locator('.track'), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details' });
    await expect(dialog).toBeVisible();
    for (const input of await dialog.locator('input').all()) {
        expect(await input.evaluate(el => parseFloat(getComputedStyle(el).fontSize))).toBeGreaterThanOrEqual(16);
    }
    const box = await dialog.boundingBox();
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(320);
    expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await capture(page, 'metadata-320');
});
