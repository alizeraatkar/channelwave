import { test, expect } from '@playwright/test';
import { installTelegramFixture, openLibrary, clickTrackAction, selectChannel } from './fixtures/telegram.mjs';

// Native Apple hosts use native storage/audio, not the browser service-worker
// adapter. This job exercises their shared Razor UI in WebKit; browser adapters
// retain their full Chromium/Edge suites and native Apple builds have a separate gate.
test.use({ serviceWorkers: 'block' });
async function seed(page) {
    await installTelegramFixture(page);
    await page.goto('/');
    await page.evaluate(async () => {
        const lib = await import('/_content/ChannelWave.Platform.Browser/channelwave.js');
        for (let i = 0; i < 12; i++) {
            const source = { accountId: 700, chatId: -100123 - i, name: i ? `Source ${i} · آهنگ‌های من` : 'Fixture', historyComplete: true };
            const tracks = i ? [] : [0, 1].map(index => ({
                id: crypto.randomUUID(), identity: `700:-100123:${index + 1}`,
                origin: { accountId: 700, chatId: -100123, messageId: index + 1, sourceName: source.name, protected: false },
                media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
                originalTitle: `Song ${index}`, originalPerformer: 'Fixture artist', title: `Song ${index}`, performer: 'Fixture artist',
                duration: 5, publishedAt: '2026-01-01T00:00:00Z', available: true, favorite: false, searchText: `song ${index} fixture artist`
            }));
            await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        }
    });
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(2);
}

for (const width of [320, 390, 820, 1440]) {
    test(`shared Apple library, picker and idle clearance at ${width}px`, async ({ page }, info) => {
        await page.setViewportSize({ width, height: 844 });
        await seed(page);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        if (width === 390) {
            const first = await page.locator('.track').first().boundingBox();
            const player = await page.locator('.idle-player').boundingBox();
            expect(first.y + first.height).toBeLessThanOrEqual(player.y);
            // The current collection toolbar needs scrolling to reach later rows.
            // Verify the last row clears the player after scrolling the content pane.
            await page.locator('.content').evaluate(element => { element.scrollTop = element.scrollHeight; });
            await expect(page.locator('.track').nth(1)).toBeInViewport({ ratio: 1 });
            const second = await page.locator('.track').nth(1).boundingBox();
            expect(second.y + second.height).toBeLessThanOrEqual(player.y);
            await page.screenshot({ path: info.outputPath('library-scrolled.png') });
            await page.locator('.content').evaluate(element => { element.scrollTop = 0; });
        }
        await page.screenshot({ path: info.outputPath('library-closed.png') });
        const trigger = page.getByLabel('Source', { exact: true });
        await trigger.click();
        const sheet = page.getByRole('dialog', { name: 'Choose a source', exact: true });
        await expect(sheet).toBeVisible();
        await expect(sheet.getByRole('radio')).toHaveCount(13);
        await page.screenshot({ path: info.outputPath('library-picker.png') });
        await page.keyboard.press('Escape');
        await expect(sheet).not.toBeVisible();
        await expect(trigger).toBeFocused();
        await selectChannel(page, '-100124');
        await expect(page.locator('.track')).toHaveCount(0);
        await expect(trigger).toContainText('Source 1');
        await selectChannel(page, '');
        await expect(page.locator('.track')).toHaveCount(2);
    });
}

test('shared Apple favorites, listening list and metadata dialogs preserve focus', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page);
    const row = page.locator('.track').first();
    await clickTrackAction(row, 'Favorite track');
    await expect(row.locator('.track-favorite')).toHaveAttribute('aria-pressed', 'true');
    await clickTrackAction(row, 'Add to playlist');
    await expect(page.getByRole('button', { name: /Listening list 1/ })).toBeVisible();
    await clickTrackAction(row, 'Edit details');
    const editor = page.getByRole('dialog', { name: 'Edit details', exact: true });
    await editor.getByLabel('Local title', { exact: true }).fill('آهنگ ویژه 🎵');
    await editor.getByRole('button', { name: 'Save local corrections', exact: true }).click();
    await expect(row.locator('.music-track-title > strong')).toHaveText('آهنگ ویژه 🎵');
    await page.keyboard.press('Escape');
    await expect(editor).not.toBeVisible();
    await expect(row.getByRole('button', { name: 'More actions', exact: true })).toBeFocused();
});

test('shared Apple enlarged text keeps the long-name picker scrollable', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 844 });
    await seed(page);
    await page.evaluate(() => {
        const sizes = [...document.querySelectorAll('button,input,h1,p,label,strong,span,bdi')]
            .map(element => [element, parseFloat(getComputedStyle(element).fontSize) * 2]);
        for (const [element, size] of sizes) element.style.fontSize = `${size}px`;
    });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByLabel('Source', { exact: true }).click();
    const sheet = page.getByRole('dialog', { name: 'Choose a source', exact: true });
    await expect(sheet.getByRole('radio', { name: /Source 11/ })).toBeAttached();
    await sheet.getByRole('radio', { name: /Source 11/ }).scrollIntoViewIfNeeded();
    await expect(sheet.getByRole('radio', { name: /Source 11/ })).toBeVisible();
    expect(await sheet.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
});
