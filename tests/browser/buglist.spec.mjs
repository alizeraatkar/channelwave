import { test, expect } from '@playwright/test';
import { expectLoadedTracks } from './fixtures/library.mjs';
import { savePreparedExport, installTelegramFixture, openLibrary, openChannels, openRoot, selectChannel, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';

async function setup(page) {
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    for (const name of [':)', 'Café Sessions', 'آهنگ‌های من 🎵']) {
        await addFixtureSource(page, name);
        await expect(page.getByRole('button', { name: `Remove from library: ${name}`, exact: true })).toBeEnabled();
    }
    await openLibrary(page);
    await expectLoadedTracks(page, 3);
}

for (const action of ['Download', 'Save offline']) {
    for (const removeOwnSource of [false, true]) {
        test(`${action} final read ${removeOwnSource ? 'is canceled for its own' : 'survives removal of an unrelated'} source (CW-QA-001)`, async ({ page }) => {
            await setup(page);
            const row = page.locator('.track').filter({ has: page.locator('.music-source', { hasText: ':)' }) });
            await page.evaluate(() => {
                const send = tdweb.default.prototype.send;
                tdweb.default.prototype.send = async function (query) {
                    const response = await send.call(this, query);
                    if (query['@type'] === 'readFile' && !globalThis.finalReadFinished) await new Promise(resolve => { globalThis.releaseFinalRead = resolve; });
                    if (query['@type'] === 'readFile') globalThis.finalReadFinished = true;
                    return response;
                };
            });
            let downloaded = false;
            page.on('download', () => { downloaded = true; });
            await clickTrackAction(row, action);
            await page.evaluate(() => completeFixtureDownload());
            await expect.poll(() => page.evaluate(() => typeof releaseFinalRead)).toBe('function');
            await openChannels(page);
            await page.getByRole('button', { name: `Remove from library: ${removeOwnSource ? ':)' : 'Café Sessions'}`, exact: true }).click();
            await openLibrary(page);
            await page.evaluate(() => releaseFinalRead());
            await expect.poll(() => page.evaluate(() => globalThis.finalReadFinished)).toBe(true);
            if (removeOwnSource) {
                await expect(page.getByRole('button', { name: 'Cancel download', exact: true })).toHaveCount(0);
                await expect(page.locator('.track').first().getByRole('button', { name: action, exact: true, includeHidden: true })).toBeEnabled();
                expect(downloaded).toBe(false);
                expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
            } else if (action === 'Download') {
                await savePreparedExport(page);
                expect(downloaded).toBe(true);
            } else {
                await expect(row.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
                await expect(page.locator('.music-feedback')).toContainText('Saved');
            }
            await expect(page.locator('.music-error')).toHaveCount(0);
        });
    }
}

test('Offline filters songs and its selector while preserving selected sidebar sources (CW-QA-005)', async ({ page }) => {
    await setup(page);
    const row = page.locator('.track').first();
    await clickTrackAction(row, 'Save offline');
    await page.evaluate(() => completeFixtureDownload());
    await expect(row.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    await page.locator('.music-tabs').getByRole('button', { name: 'Offline', exact: true }).click();
    await expect(page.locator('.track')).toHaveCount(1);
    await page.getByLabel('Source', { exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Choose a source', exact: true }).getByRole('radio')).toHaveCount(2);
    await page.keyboard.press('Escape');
    await openRoot(page, 'Library');
    await expect(page.getByRole('button', { name: 'Sources 3 sources', exact: true })).toBeVisible();
    await openLibrary(page);
    await selectChannel(page, '-100124');
    await expect(page.getByLabel('Source', { exact: true })).toContainText('Café Sessions');
    await expect(page.locator('.track')).toHaveCount(1);
    await expect(page.locator('.track .music-source')).toHaveText(/Café Sessions/);
});

test('removing and readding a source cannot revive its pending offline save (CW-QA-001)', async ({ page }) => {
    await setup(page);
    await page.evaluate(async path => {
        const lib = await import(path);
        const source = JSON.parse(await lib.readSource(700, -100123));
        const track = JSON.parse(await lib.searchLibrary(700, '', 0, 100)).find(track => track.origin.chatId === source.chatId);
        globalThis.readdSource = source;
        globalThis.readdTrack = track;
        const send = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            const response = await send.call(this, query);
            if (query['@type'] === 'readFile') await new Promise(resolve => { globalThis.releaseReaddRead = resolve; });
            return response;
        };
        globalThis.pendingSave = lib.saveOffline(JSON.stringify(track), 1, 80044).then(() => 'saved', error => error.message);
    }, modulePath);
    await expect.poll(() => page.evaluate(() => typeof releaseReaddRead)).toBe('function');
    await page.evaluate(async path => {
        const lib = await import(path);
        await lib.removeSource(700, -100123);
        await lib.savePage(JSON.stringify(readdSource), JSON.stringify([readdTrack]));
        releaseReaddRead();
    }, modulePath);
    expect(await page.evaluate(() => pendingSave)).toContain('canceled');
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
});

test('browser Back and Forward restore app screens and collection filters (CW-QA-006)', async ({ page }) => {
    await setup(page);
    await expect(page).toHaveURL(/#view=tracks&browse=/);
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Find your next listen.' })).toBeVisible();
    await page.goBack();
    await expect(page.getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await page.goForward();
    await expect(page.getByRole('heading', { name: 'Find your next listen.' })).toBeVisible();
    await openLibrary(page);
    await page.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true }).click();
    await expect(page).toHaveURL(/#view=favorites&browse=/);
    await page.goBack();
    await expect(page.locator('.music-tabs').getByRole('button', { name: 'All tracks', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await page.goForward();
    await expect(page.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await page.reload();
    // The synthetic session reconnects explicitly after reload; its restored collection remains hidden until then.
    await expect(page.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true, includeHidden: true })).toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('button', { name: 'ChannelWave home', exact: true }).click();
    await expect(page).toHaveURL(/#view=home(?:&browse=|$)/);
});

for (const viewport of [{ width: 390, height: 844 }, { width: 320, height: 568 }, { width: 844, height: 390 }]) {
    test(`keyboard focus stays above fixed controls at ${viewport.width}x${viewport.height} (CW-QA-002)`, async ({ page }) => {
        await page.setViewportSize(viewport);
        await setup(page);
        await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
        await page.locator('#main-content').focus();
        let checked = 0;
        for (let i = 0; i < 45; i++) {
            await page.keyboard.press('Tab');
            await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
            const focus = await page.evaluate(() => {
                const target = document.activeElement;
                if (!target.closest('.music-content') || target.disabled) return null;
                const rect = target.getBoundingClientRect();
                const overlays = [...document.querySelectorAll('.music-player, .mobile-nav')].map(el => el.getBoundingClientRect()).filter(rect => rect.height > 0);
                const hit = document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2);
                return { name: target.outerHTML, left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, limit: Math.min(innerHeight, ...overlays.map(rect => rect.top)), hit: target.contains(hit), covering: hit?.outerHTML };
            });
            if (!focus) continue;
            checked++;
            expect(focus.top).toBeGreaterThanOrEqual(0);
            expect(focus.bottom).toBeLessThanOrEqual(focus.limit);
            expect(focus.hit, JSON.stringify(focus)).toBe(true);
        }
        expect(checked).toBeGreaterThan(20);
        await page.screenshot({ path: `artifacts/qa-fixes/focus-${viewport.width}.png` });
    });
}

test('short landscape sidebar scrolls entirely above the player (CW-QA-004)', async ({ page }) => {
    await page.setViewportSize({ width: 844, height: 390 });
    await setup(page);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
    const sidebar = await page.locator('.app-sidebar').boundingBox();
    const player = await page.locator('.music-player').boundingBox();
    expect(sidebar.y + sidebar.height).toBeLessThanOrEqual(player.y);
    const target = page.getByRole('navigation', { name: 'Main navigation' }).getByRole('button', { name: 'Profile', exact: true });
    await target.scrollIntoViewIfNeeded();
    const bounds = await target.boundingBox();
    const scroller = await page.locator('.app-sidebar').boundingBox();
    expect(bounds.y).toBeGreaterThanOrEqual(scroller.y);
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(scroller.y + scroller.height + 1);
    expect(await target.evaluate(element => {
        const rect = element.getBoundingClientRect();
        return [2, rect.height / 2, rect.height - 2].every(offset => element.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + offset)));
    })).toBe(true);
    await page.screenshot({ path: 'artifacts/qa-fixes/sidebar-landscape.png' });
});

test('320px header preserves the account target at 200% text (CW-QA-003)', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 568 });
    await setup(page);
    await openRoot(page, 'Library');
    await page.evaluate(() => {
        const elements = [...document.querySelectorAll('h1,h2,h3,p,button,label,select,strong,small,span')];
        const sizes = elements.map(el => parseFloat(getComputedStyle(el).fontSize) * 2);
        elements.forEach((el, i) => { el.style.fontSize = `${sizes[i]}px`; });
    });
    const account = await page.getByRole('button', { name: 'Profile', exact: true }).boundingBox();
    const settings = await page.getByRole('button', { name: 'Settings', exact: true }).boundingBox();
    expect(settings.x + settings.width).toBeLessThanOrEqual(account.x + 1);
    expect(account.x + account.width).toBeLessThanOrEqual(320);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320);
    await page.screenshot({ path: 'artifacts/qa-fixes/header-200.png' });
});

test('duration renders total elapsed hours beyond a day (CW-QA-008)', async ({ page }) => {
    await setup(page);
    await page.evaluate(async path => {
        const lib = await import(path);
        const source = JSON.parse(await lib.readSource(700, -100123));
        const tracks = JSON.parse(await lib.searchLibrary(700, '', 0, 100)).filter(track => track.origin.chatId === source.chatId);
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks.map(track => ({ ...track, duration: 90000 }))));
    }, modulePath);
    await openLibrary(page);
    await expect(page.locator('.music-file-details').filter({ hasText: '25:00:00' })).toHaveCount(1);
});
