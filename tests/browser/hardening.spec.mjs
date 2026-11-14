import { openRoot } from './fixtures/telegram.mjs';
import { expectLoadedTracks } from './fixtures/library.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture, openLibrary, clickTrackAction, selectChannel, connectFixture } from './fixtures/telegram.mjs';
import fs from 'node:fs/promises';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const library = page => page.getByRole('region', { name: 'Local library', exact: true });
test.beforeEach(async ({ page }) => {
    page.unhandledErrors = [];
    page.on('pageerror', error => page.unhandledErrors.push(error.message));
});
test.afterEach(async ({ page }) => {
    expect(page.unhandledErrors).toEqual([]);
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
async function seeded(page, count = 3) {
    await installTelegramFixture(page);
    await page.goto('/');
    await page.evaluate(async ({ modulePath, count }) => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -100123, name: 'Fixture', historyComplete: true };
        const tracks = Array.from({ length: count }, (_, i) => ({
            id: crypto.randomUUID(), identity: `700:-100123:${String(i).padStart(5, '0')}`,
            origin: { accountId: 700, chatId: -100123, messageId: i, sourceName: 'Fixture', protected: false },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
            originalTitle: `Song ${i}`, originalPerformer: 'Fixture artist', title: `Song ${i}`, performer: 'Fixture artist',
            duration: 5, publishedAt: '2026-01-01T00:00:00+00:00', available: true, favorite: i === count - 1, searchText: `song ${i} fixture artist`
        }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        globalThis.seededIds = tracks.map(t => t.id);
    }, { modulePath, count });
    await connectFixture(page);
    // Mobile Library opens Offline while disconnected; wait for connection restoration.
    await expect(library(page).getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await openLibrary(page);
    await expectLoadedTracks(page, Math.min(count, 100));
}

test('favorites and listening list find songs beyond the first page', async ({ page }) => {
    await seeded(page, 150);
    await library(page).getByRole('button', { name: 'Liked Songs', exact: true }).click();
    await expect(library(page).locator('.track')).toHaveCount(1);
    await expect(library(page).locator('.track')).toContainText('Song 149');
    await clickTrackAction(library(page), 'Add to playlist');
    await library(page).getByRole('button', { name: 'All tracks', exact: true }).click();
    await library(page).getByRole('button', { name: /Listening list 1/ }).click();
    await expect(library(page).locator('.track')).toHaveCount(1);
    await expect(library(page).locator('.track')).toContainText('Song 149');
});

test('channel filters find a source beyond the first page and pagination has no empty extra page', async ({ page }) => {
    await seeded(page, 101);
    await page.locator('#main-content').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await expectLoadedTracks(page, 101);
    await expect(library(page).getByRole('button', { name: 'Load more tracks', exact: true })).toHaveCount(0);
    await page.evaluate(async p => {
        const lib = await import(p);
        const template = JSON.parse(await lib.searchLibrary(700, '', 0, 1))[0];
        const source = { accountId: 700, chatId: 5, name: 'Later source', historyComplete: true };
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...template, id: crypto.randomUUID(), identity: '700:5:1',
            origin: { ...template.origin, chatId: 5, sourceName: source.name }, title: 'Later song' }]));
    }, modulePath);
    await openLibrary(page);
    await selectChannel(library(page), '5');
    await expect(library(page).locator('.track')).toHaveCount(1);
    await expect(library(page).locator('.track')).toContainText('Later song');
});

test('empty favorites return to existing music and navigation has keyboard focus', async ({ page }) => {
    await seeded(page);
    await library(page).locator('.track').last().getByRole('button', { name: 'Remove favorite', exact: true }).click();
    await library(page).getByRole('button', { name: 'Liked Songs', exact: true }).click();
    await expect(library(page).locator('.track')).toHaveCount(0);
    await expect(page.locator('#main-content')).toBeFocused();
    await library(page).getByRole('button', { name: 'Show all tracks', exact: true }).click();
    await expect(library(page).locator('.track')).toHaveCount(3);
    await page.getByRole('button', { name: 'ChannelWave home', exact: true }).click();
    await page.reload();
    await expect(page.getByRole('navigation', { name: 'Main navigation' }).getByRole('button', { name: 'Home', exact: true })).toHaveAttribute('aria-current', 'page');
    await page.getByRole('link', { name: 'Skip to content' }).focus();
    await expect(page.getByRole('link', { name: 'Skip to content' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator('#main-content')).toBeFocused();
});

test('listening list keeps insertion order and supports removal without duplicates', async ({ page }) => {
    await seeded(page);
    await clickTrackAction(library(page).locator('.track').nth(2), 'Add to playlist');
    await clickTrackAction(library(page).locator('.track').nth(0), 'Add to playlist');
    await library(page).getByRole('button', { name: /Listening list 2/ }).click();
    await expect(library(page).locator('.music-track-title > strong')).toHaveText(['Song 2', 'Song 0']);
    await clickTrackAction(library(page).locator('.track').first(), 'Remove from playlist');
    await expect(library(page).locator('.music-track-title > strong')).toHaveText(['Song 0']);
    await page.reload();
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await openLibrary(page);
    await library(page).getByRole('button', { name: /Listening list 1/ }).click();
    await expect(library(page).locator('.music-track-title > strong')).toHaveText(['Song 0']);
});

test('failed list save leaves no optimistic membership or success message', async ({ page }) => {
    await seeded(page);
    await page.evaluate(() => {
        const put = IDBObjectStore.prototype.put;
        globalThis.restoreFixtureWrites = () => { IDBObjectStore.prototype.put = put; };
        IDBObjectStore.prototype.put = function (value, ...args) {
            if (this.name === 'state' && value.key === 'playlist') throw new DOMException('Synthetic full storage', 'QuotaExceededError');
            return put.call(this, value, ...args);
        };
    });
    await clickTrackAction(library(page).locator('.track').first(), 'Add to playlist');
    await expect(page.getByRole('alert')).toContainText('Synthetic full storage');
    await expect(library(page).getByRole('button', { name: /Listening list 0/ })).toBeVisible();
    await expect(library(page).locator('.music-feedback')).toHaveCount(0);
    const add = library(page).locator('.track').first().getByRole('button', { name: 'Add to playlist', exact: true, includeHidden: true });
    await expect(add).toBeEnabled();
    await page.evaluate(() => restoreFixtureWrites());
    await clickTrackAction(library(page).locator('.track').first(), 'Add to playlist');
    await expect(library(page).getByRole('button', { name: /Listening list 1/ })).toBeVisible();
    await expect(library(page).getByRole('alert')).toHaveCount(0);
});

test('playback sequence survives searching and navigation', async ({ page }) => {
    await seeded(page);
    await library(page).locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('status')).toContainText('Streaming');
    await player.getByRole('button', { name: 'Pause playback' }).click();
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await library(page).getByRole('searchbox').fill('missing song');
    await library(page).getByRole('button', { name: 'Search', exact: true }).click();
    await expect(library(page).locator('.track')).toHaveCount(0);
    await expect(player.getByRole('button', { name: 'Next track', exact: true })).toBeEnabled();
    await player.getByRole('button', { name: 'Next track', exact: true }).click();
    await expect(player.locator('.music-player-info strong')).toHaveText('Song 1');
    await expect(player.getByRole('status')).toContainText('Streaming');
});

test('a transient audio-state failure does not stop progress polling', async ({ page }) => {
    await seeded(page);
    await library(page).locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('status')).toContainText('Streaming');
    await player.getByRole('button', { name: 'Pause playback' }).click();
    await page.evaluate(() => {
        const duration = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'duration');
        let failed = false;
        Object.defineProperty(fixtureAudio, 'duration', { configurable: true, get() {
            if (!failed) { failed = true; throw new Error('Synthetic transient audio state'); }
            return duration.get.call(this);
        } });
    });
    await expect(player.getByRole('alert')).toContainText('Synthetic transient audio state');
    await page.evaluate(() => { fixtureAudio.currentTime = 3; });
    await expect(player.getByLabel('Playback position', { exact: true })).toHaveValue('3');
    await player.getByRole('button', { name: 'Resume playback' }).click();
    await expect(player.getByRole('alert')).toHaveCount(0);
});

test('seek and volume failures stay recoverable without crashing the app', async ({ page }) => {
    await seeded(page);
    await library(page).locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('status')).toContainText('Streaming');
    await player.getByRole('button', { name: 'Pause playback' }).click();
    await page.evaluate(() => {
        const time = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'currentTime');
        Object.defineProperty(fixtureAudio, 'currentTime', { configurable: true, get() { return time.get.call(this); }, set() { throw new Error('Synthetic seek failure'); } });
    });
    await player.getByLabel('Playback position', { exact: true }).fill('2');
    await expect(player.getByRole('alert')).toContainText('Synthetic seek failure');
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
    await page.evaluate(() => {
        delete fixtureAudio.currentTime;
        const volume = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'volume');
        Object.defineProperty(fixtureAudio, 'volume', { configurable: true, get() { return volume.get.call(this); }, set() { throw new Error('Synthetic volume failure'); } });
    });
    await player.getByLabel('Volume', { exact: true }).fill('0.5');
    await expect(player.getByRole('alert')).toContainText('Synthetic volume failure');
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
    await page.evaluate(() => { delete fixtureAudio.volume; });
    await player.getByRole('button', { name: 'Stop playback' }).click();
    await expect(player).toHaveCount(0);
});

test('failed sign-in preserves the entered phone number for correction', async ({ page }) => {
    await installTelegramFixture(page, 'auth');
    await page.addInitScript(() => {
        const send = tdweb.default.prototype.send;
        tdweb.default.prototype.send = function (q) {
            if (q['@type'] === 'setAuthenticationPhoneNumber') throw { code: 400, message: 'PHONE_NUMBER_INVALID' };
            return send.call(this, q);
        };
    });
    await page.goto('/');
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    const phone = page.getByLabel('Phone number with country code', { exact: true });
    await phone.fill('+15550100000');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(phone).toHaveValue('+15550100000');
});

test('pointer playback activation survives focus settling near the fixed player', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 568 });
    await seeded(page);
    const play = library(page).locator('.track').first().getByRole('button', { name: 'Stream', exact: true });
    await play.scrollIntoViewIfNeeded();
    const bounds = await play.boundingBox();
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await page.mouse.down();
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await page.mouse.up();
    await expect(page.getByRole('region', { name: 'Now playing', exact: true }).getByRole('status')).toContainText('Streaming');
});

for (const viewport of [{ width: 320, height: 568 }, { width: 760, height: 800 }, { width: 820, height: 1180 }, { width: 844, height: 390 }]) {
    test(`library and player fit ${viewport.width}x${viewport.height}`, async ({ page }) => {
        await page.setViewportSize(viewport);
        await seeded(page);
        await library(page).locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        const player = page.getByRole('region', { name: 'Now playing', exact: true });
        await expect(player.getByRole('status')).toContainText('Streaming');
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        for (const button of await player.getByRole('button').all()) {
            const box = await button.boundingBox();
            expect(box.x).toBeGreaterThanOrEqual(0);
            expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
            expect(box.y).toBeGreaterThanOrEqual(0);
            expect(box.y + box.height).toBeLessThanOrEqual(viewport.height);
        }
        await fs.mkdir('artifacts/browser-review', { recursive: true });
        await page.screenshot({ path: `artifacts/browser-review/audit-player-${viewport.width}.png` });
    });
}

test('long multilingual metadata and enlarged text remain readable at phone width', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await seeded(page);
    await page.evaluate(async p => {
        const lib = await import(p);
        const tracks = JSON.parse(await lib.searchLibrary(700, '', 0, 100));
        const source = JSON.parse(await lib.readSources(700))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks.map(t => ({ ...t,
            title: 'آهنگ‌های بسیار طولانی 🎵 音楽 '.repeat(12), performer: 'A performer with a very long name '.repeat(10) }))));
    }, modulePath);
    await openLibrary(page);
    await library(page).locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('status')).toContainText('Streaming');
    await player.getByRole('button', { name: 'Pause playback' }).click();
    await page.evaluate(() => {
        const changes = [...document.querySelectorAll('button,input,select,h1,h2,h3,p,label,strong,span,bdi')]
            .map(el => [el, parseFloat(getComputedStyle(el).fontSize) * 2]);
        for (const [el, size] of changes) el.style.fontSize = `${size}px`;
    });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect.poll(async () => {
        const controlBounds = await player.boundingBox();
        return controlBounds.y >= 0 && controlBounds.y + controlBounds.height <= 845;
    }).toBe(true);
    await expect(player.getByLabel('Playback position', { exact: true })).toBeVisible();
    await fs.mkdir('artifacts/browser-review', { recursive: true });
    await page.screenshot({ path: 'artifacts/browser-review/audit-text-200.png' });
});
