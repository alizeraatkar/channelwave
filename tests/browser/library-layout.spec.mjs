import { openRoot } from './fixtures/telegram.mjs';
import { expectLoadedTracks } from './fixtures/library.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, openLibrary, clickTrackAction, selectChannel, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

async function seed(page, { count = 2, channels = 2, long = false } = {}) {
    await installTelegramFixture(page);
    await page.goto('/');
    await page.evaluate(async ({ count, channels, long }) => {
        const lib = await import('/_content/ChannelWave.Platform.Browser/channelwave.js');
        for (let i = 0; i < channels; i++) {
            const name = long ? `${i} آهنگ‌های من — Recordings and performances from around the world` : `Channel ${i}`;
            const source = { accountId: 700, chatId: -100123 - i, name, historyComplete: true };
            const tracks = i ? [] : Array.from({ length: count }, (_, index) => ({
                id: crypto.randomUUID(), identity: `700:-100123:${String(index).padStart(5, '0')}`,
                origin: { accountId: 700, chatId: source.chatId, messageId: index + 1, sourceName: name, protected: false },
                media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
                originalTitle: 'Fixture', originalPerformer: 'Fixture artist',
                title: long ? 'آهنگ من — A long recording title from a live performance' : `Song ${index}`,
                performer: long ? 'هنرمند من — A long performer name' : 'Fixture artist',
                duration: 5, publishedAt: '2026-01-01T00:00:00+00:00', available: true, favorite: false, searchText: `song ${index}`
            }));
            await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        }
    }, { count, channels, long });
    await connectFixture(page);
    await expect(page.getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await openLibrary(page);
    await expectLoadedTracks(page, Math.min(100, count));
}

async function capture(page, name) {
    await fs.mkdir('artifacts/browser-review', { recursive: true });
    await page.screenshot({ path: `artifacts/browser-review/library-redesign-${name}.png` });
}

for (const width of [320, 390, 820, 1440]) {
    test(`compact library and themed picker fit at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 844 });
        await seed(page);
        const first = await page.locator('.track').first().boundingBox();
        const idle = await page.locator('.idle-player').boundingBox();
        if (width === 390) {
            expect(first.y + first.height).toBeLessThan(idle.y);
            expect(idle.height).toBe(56);
            await page.locator('.content').evaluate(element => { element.scrollTop = element.scrollHeight; });
            await expect(page.locator('.track').last()).toBeInViewport({ ratio: 1 });
            const last = await page.locator('.track').last().boundingBox();
            expect(last.y + last.height).toBeLessThan(idle.y);
            await capture(page, `scrolled-${width}`);
            await page.locator('.content').evaluate(element => { element.scrollTop = 0; });
        }
        expect(first.height).toBeLessThanOrEqual(120);
        for (const button of await page.locator('.track').first().getByRole('button').all()) {
            const box = await button.boundingBox();
            expect(box.width).toBeGreaterThanOrEqual(44);
            expect(box.height).toBeGreaterThanOrEqual(44);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await capture(page, `closed-${width}`);
        const trigger = page.getByLabel('Source', { exact: true });
        await trigger.click();
        const dialog = page.getByRole('dialog', { name: 'Choose a source', exact: true });
        await expect(dialog).toBeVisible();
        await expect(dialog.getByRole('radio')).toHaveCount(3);
        await expect(dialog.getByRole('radio', { name: 'All sources', exact: true })).toBeFocused();
        const bounds = await dialog.boundingBox();
        if (width <= 760) expect(bounds.y + bounds.height).toBeCloseTo(844, 0);
        else expect(bounds.width).toBeLessThanOrEqual(420);
        await capture(page, `picker-${width}`);
        await page.keyboard.press('Escape');
        await expect(dialog).not.toBeVisible();
        await expect(trigger).toBeFocused();
    });
}

test('picker filters immediately, preserves selection, and restores focus on dismissal', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page);
    await selectChannel(page, '-100124');
    await expect(page.locator('.track')).toHaveCount(0);
    const trigger = page.getByLabel('Source', { exact: true });
    await expect(trigger).toContainText('Channel 1');
    await trigger.click();
    const dialog = page.getByRole('dialog', { name: 'Choose a source', exact: true });
    await expect(dialog.getByRole('radio', { name: 'Channel 1', exact: true })).toBeChecked();
    await page.mouse.click(4, 4);
    await expect(dialog).not.toBeVisible();
    await expect(trigger).toBeFocused();
    await trigger.click();
    expect(await page.evaluate(() => window.channelwaveDismissDialog())).toBe(true);
    await expect(dialog).not.toBeVisible();
    expect(await page.evaluate(() => window.channelwaveDismissDialog())).toBe(false);
    await selectChannel(page, '');
    await expect(page.locator('.track')).toHaveCount(2);
});

test('phone More actions preserve list membership and metadata focus restoration', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page);
    const row = page.locator('.track').first();
    await clickTrackAction(row, 'Add to playlist');
    await expect(page.getByRole('button', { name: /Listening list 1/ })).toBeVisible();
    await expect(row.getByRole('button', { name: 'More actions' })).toBeFocused();
    await clickTrackAction(row, 'Edit details');
    const editor = page.getByRole('dialog', { name: 'Edit details', exact: true });
    await expect(editor).toBeVisible();
    await expect(page.getByRole('dialog', { name: 'Song actions' })).not.toBeVisible();
    await page.keyboard.press('Escape');
    await expect(editor).not.toBeVisible();
    await expect(row.getByRole('button', { name: 'More actions' })).toBeFocused();
});

test('long bilingual names and enlarged text fit with a scrolling channel sheet', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 844 });
    await seed(page, { channels: 35, long: true });
    await page.evaluate(() => {
        const elements = [...document.querySelectorAll('.brand strong, .music-heading h1, .music-count, .music-tabs button, .music-track-title strong, .music-artist, .music-source, .music-file-details, .idle-player strong')];
        const sizes = elements.map(el => parseFloat(getComputedStyle(el).fontSize));
        elements.forEach((el, i) => { el.style.fontSize = `${sizes[i] * 2}px`; });
    });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByLabel('Source', { exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Choose a source', exact: true });
    expect(await dialog.locator('.library-dialog-content').evaluate(el => el.scrollHeight > el.clientHeight)).toBe(true);
    const last = dialog.getByRole('radio').last();
    await last.focus();
    const radio = await last.boundingBox();
    const bounds = await dialog.boundingBox();
    expect(radio.y).toBeGreaterThanOrEqual(bounds.y);
    expect(radio.y + radio.height).toBeLessThanOrEqual(bounds.y + bounds.height);
    await capture(page, 'large-text-picker-320');
});

test('last track remains reachable above fixed controls before and during playback', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page, { count: 15 });
    const last = page.getByRole('article', { name: 'Song 14 by Fixture artist', exact: true });
    const panel = page.locator('#main-content');
    for (const active of [false, true]) {
        if (active) {
            await panel.evaluate(el => { el.scrollTop = 0; });
            await page.getByRole('article', { name: 'Song 0 by Fixture artist', exact: true }).getByRole('button', { name: 'Stream', exact: true }).click();
            await expect(page.locator('.music-player-status')).toContainText('Streaming');
            await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
        }
        await panel.evaluate(el => { el.scrollTop = el.scrollHeight; });
        await last.getByRole('button', { name: 'More actions' }).focus();
        await expect.poll(async () => {
            const row = await last.boundingBox();
            const dock = await page.locator(active ? '.music-player' : '.idle-player').boundingBox();
            return row.y >= 0 && row.y + row.height < dock.y;
        }).toBe(true);
    }
});

test('phone action sheet retains protected-content restrictions', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await installTelegramFixture(page, 'protected');
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page);
    const row = page.locator('.track');
    await expect(row.getByRole('button', { name: 'Stream', exact: true })).toBeDisabled();
    await row.getByRole('button', { name: 'More actions' }).click();
    const dialog = page.getByRole('dialog', { name: 'Song actions', exact: true });
    await expect(dialog.getByRole('button', { name: 'Save offline', exact: true })).toBeDisabled();
    await expect(dialog.getByRole('button', { name: 'Download', exact: true })).toBeDisabled();
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
});

test('navigation dismisses stale source and action dialogs', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page);
    for (const trigger of [() => page.getByLabel('Source', { exact: true }), () => page.locator('.track').first().getByRole('button', { name: 'More actions' })]) {
        await trigger().click();
        await expect(page.locator('dialog[open]')).toHaveCount(1);
        await page.evaluate(() => { location.hash = '#view=account'; });
        await expect(page.getByRole('heading', { name: 'Profile', exact: true })).toBeVisible();
        await expect(page.locator('dialog[open]')).toHaveCount(0);
        await expect(page.locator('html')).not.toHaveClass(/modal-open/);
        await openLibrary(page);
    }
});

for (const viewport of [{ width: 390, height: 844 }, { width: 1440, height: 900 }, { width: 844, height: 390 }]) {
    test(`library panel scrolls without moving the app frame at ${viewport.width}x${viewport.height}`, async ({ page }) => {
        await page.setViewportSize(viewport);
        await seed(page, { count: 105 });
        const panel = page.locator('#main-content');
        const header = page.locator('.app-topbar');
        const headerBefore = await header.boundingBox();
        const idleBefore = await page.locator('.idle-player').boundingBox();
        const nav = page.locator('.app-sidebar');
        const navBefore = viewport.width > 760 ? await nav.boundingBox() : null;
        if (viewport.width <= 760) await expect(page.locator('.mobile-nav')).toHaveCount(0);
        const bounds = await panel.boundingBox();
        await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
        await page.mouse.wheel(0, 650);
        await expect.poll(() => panel.evaluate(el => el.scrollTop)).toBeGreaterThan(0);
        if (headerBefore) expect((await header.boundingBox()).y).toBe(headerBefore.y);
        else await expect(header).toBeHidden();
        if (navBefore) expect((await nav.boundingBox()).y).toBe(navBefore.y);
        expect((await page.locator('.idle-player').boundingBox()).y).toBe(idleBefore.y);
        expect(await page.evaluate(() => ({ y: scrollY, height: document.documentElement.scrollHeight }))).toEqual({ y: 0, height: viewport.height });

        await page.keyboard.press('Tab');
        await panel.evaluate(el => { el.scrollTop = el.scrollHeight; });
        await expectLoadedTracks(page, 105);
        await panel.evaluate(el => { el.scrollTop = el.scrollHeight; });
        await page.getByRole('article', { name: 'Song 104 by Fixture artist', exact: true }).getByRole('button', { name: 'More actions' }).focus();
        await expect.poll(async () => {
            const last = await page.getByRole('article', { name: 'Song 104 by Fixture artist', exact: true }).boundingBox();
            const content = await panel.boundingBox();
            return last.y >= content.y && last.y + last.height <= content.y + content.height;
        }).toBe(true);
        const beforeDialog = await panel.evaluate(el => el.scrollTop);
        await page.getByRole('article', { name: 'Song 104 by Fixture artist', exact: true }).getByRole('button', { name: 'More actions' }).click();
        await expect(page.getByRole('dialog', { name: 'Song actions' })).toBeVisible();
        await expect(panel).toHaveCSS('overflow-y', 'hidden');
        await page.keyboard.press('Escape');
        await expect(page.getByRole('dialog', { name: 'Song actions' })).not.toBeVisible();
        expect(await panel.evaluate(el => el.scrollTop)).toBe(beforeDialog);

        await openRoot(page, 'Profile');
        await expect(page.getByRole('heading', { name: 'Profile', exact: true })).toBeVisible();
        await expect.poll(() => panel.evaluate(el => el.scrollTop)).toBe(0);
    });
}

test('touch swipes scroll the library panel and preserve fixed phone controls', async ({ browser, browserName }) => {
    test.skip(browserName !== 'chromium', 'Touch injection uses Chromium DevTools; physical device validation is separate.');
    const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    try {
        await seed(page, { count: 30 });
        const client = await context.newCDPSession(page);
        const header = await page.locator('.app-topbar').boundingBox();
        const idle = await page.locator('.idle-player').boundingBox();
        await client.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: 180, y: 620 }] });
        for (const y of [570, 520, 470, 420, 370, 320]) {
            await client.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: 180, y }] });
        }
        await client.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
        await expect.poll(() => page.locator('#main-content').evaluate(el => el.scrollTop)).toBeGreaterThan(100);
        expect(await page.locator('.app-topbar').boundingBox()).toEqual(header);
        await expect(page.locator('.mobile-nav')).toHaveCount(0);
        expect((await page.locator('.idle-player').boundingBox()).y).toBe(idle.y);
        expect(await page.evaluate(() => scrollY)).toBe(0);
    } finally {
        await context.close();
    }
});

test('playback errors and transfers resize the panel without hiding the final song', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page, { count: 25 });
    const panel = page.locator('#main-content');
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(player.getByRole('status')).toContainText('Streaming');
    await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
    await page.evaluate(() => {
        Object.defineProperty(fixtureAudio, 'currentTime', { configurable: true, get() { return 1; }, set() { throw new Error('Synthetic scrolling test seek failure'); } });
    });
    await player.getByRole('slider', { name: 'Playback position' }).fill('2');
    await expect(player.getByRole('alert')).toContainText('Synthetic scrolling test seek failure');
    await clickTrackAction(page.locator('.track').nth(1), 'Download');
    await expect(page.getByRole('region', { name: 'Active transfer' })).toBeVisible();
    await page.keyboard.press('Shift');
    await panel.evaluate(el => { el.scrollTop = el.scrollHeight; });
    const lastRow = page.getByRole('article', { name: 'Song 24 by Fixture artist', exact: true });
    await lastRow.getByRole('button', { name: 'More actions' }).focus();
    await expect.poll(async () => {
        const last = await lastRow.boundingBox();
        const content = await panel.boundingBox();
        const transfer = await page.locator('.music-transfer').boundingBox();
        return last.y >= content.y && last.y + last.height <= content.y + content.height && content.y + content.height < transfer.y;
    }).toBe(true);
    await page.getByRole('button', { name: 'Cancel download' }).click();
    await expect(page.locator('.music-transfer')).toHaveCount(0);
    await page.evaluate(() => { delete fixtureAudio.currentTime; });
});
