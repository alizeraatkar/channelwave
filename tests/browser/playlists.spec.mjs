import { openRoot } from './fixtures/telegram.mjs';
import { expectLoadedTracks } from './fixtures/library.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture, openLibrary, clickTrackAction, stopPlayback, connectFixture } from './fixtures/telegram.mjs';
import fs from 'node:fs/promises';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const library = page => page.getByRole('region', { name: 'Local library', exact: true });
const manager = page => page.getByRole('region', { name: 'Playlist management' });
const titles = page => library(page).locator('.music-track-title > strong');
const picker = page => page.getByRole('dialog', { name: 'Add to playlist', exact: true });

async function seeded(page, count = 4, withPlaylist = false) {
    await installTelegramFixture(page);
    await page.goto('/');
    await page.evaluate(async ({ modulePath, count, withPlaylist }) => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -100123, name: 'Fixture source', historyComplete: true };
        const tracks = Array.from({ length: count }, (_, i) => ({
            id: crypto.randomUUID(), identity: `700:-100123:${String(i).padStart(5, '0')}`,
            origin: { accountId: 700, chatId: -100123, messageId: i, sourceName: source.name, protected: count === 150 && i !== 0 && i !== count - 1 },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
            originalTitle: `Song ${String(i).padStart(3, '0')}`, originalPerformer: `Artist ${i % 2}`,
            title: `Song ${String(i).padStart(3, '0')}`, performer: `Artist ${i % 2}`, duration: 5,
            publishedAt: `2026-01-01T00:${String(Math.floor(i / 60)).padStart(2, '0')}:${String(i % 60).padStart(2, '0')}+00:00`,
            available: true, favorite: false, searchText: `song ${String(i).padStart(3, '0')} artist ${i % 2} fixture source`
        }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        globalThis.playlistIds = tracks.map(t => t.id);
        if (withPlaylist) {
            globalThis.fixturePlaylistId = crypto.randomUUID();
            await lib.saveState(700, 'playlists', JSON.stringify([{ Id: fixturePlaylistId, Name: 'Evening collection', Tracks: tracks.map(t => t.id).reverse() }]));
        }
    }, { modulePath, count, withPlaylist });
    await connectFixture(page);
    await expect(library(page).getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await openLibrary(page);
    await expectLoadedTracks(page, Math.min(count, 100));
}
async function openLists(page) {
    await library(page).getByRole('button', { name: /^Listening list \d/ }).click();
    await expect(manager(page)).toBeVisible();
}
async function chooseNamed(page) {
    await openLists(page);
    await manager(page).getByLabel('Choose playlist', { exact: true }).selectOption(await page.evaluate(() => fixturePlaylistId));
    await expect(library(page).getByRole('heading', { name: 'Evening collection', exact: true })).toBeVisible();
}
async function create(page, name) {
    await manager(page).getByRole('button', { name: 'New playlist', exact: true }).click();
    await manager(page).getByLabel('New playlist name', { exact: true }).fill(name);
    await manager(page).getByRole('button', { name: 'Save playlist', exact: true }).click();
    await expect(library(page).getByRole('heading', { name, exact: true })).toBeVisible();
}
async function action(page, name) {
    if (!await manager(page).getByRole('button', { name, exact: true }).isVisible())
        await manager(page).getByRole('button', { name: 'Playlist actions', exact: true }).click();
    await manager(page).getByRole('button', { name, exact: true }).click();
}
test.afterEach(async ({ page }) => {
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('named playlists support bulk add, reorder, rename, duplicate, delete and restart', async ({ page }) => {
    await seeded(page);
    await openLists(page);
    await create(page, 'Night drive');
    await create(page, 'Second playlist');
    await expect(manager(page).getByLabel('Choose playlist').locator('option')).toHaveText(['Listening list · 0 songs', 'Night drive · 0 songs', 'Second playlist · 0 songs']);
    await action(page, 'Delete playlist');
    await manager(page).getByRole('button', { name: 'Delete', exact: true }).click();
    await library(page).getByRole('button', { name: 'Find songs to add', exact: true }).click();
    await library(page).getByRole('button', { name: 'Select tracks', exact: true }).click();
    await library(page).getByLabel('Select Song 002', { exact: true }).check();
    await library(page).getByLabel('Select Song 000', { exact: true }).check();
    await library(page).getByRole('button', { name: 'Add selected to playlist', exact: true }).click();
    await picker(page).getByRole('button', { name: /Night drive/ }).click();
    await expect(picker(page)).not.toBeVisible();
    await openLists(page);
    await manager(page).getByLabel('Choose playlist', { exact: true }).selectOption({ label: 'Night drive · 2 songs' });
    await expect(titles(page)).toHaveText(['Song 002', 'Song 000']);
    await library(page).getByRole('button', { name: 'Organize tracks', exact: true }).click();
    await library(page).getByRole('button', { name: 'Move Song 000 up', exact: true }).click();
    await expect(titles(page)).toHaveText(['Song 000', 'Song 002']);
    await action(page, 'Rename');
    await manager(page).getByLabel('Playlist name', { exact: true }).fill('Late night');
    await manager(page).getByRole('button', { name: 'Save playlist', exact: true }).click();
    await expect(library(page).getByRole('heading', { name: 'Late night', exact: true })).toBeVisible();
    await action(page, 'Duplicate');
    await manager(page).getByRole('button', { name: 'Save playlist', exact: true }).click();
    await expect(library(page).getByRole('heading', { name: 'Late night copy', exact: true })).toBeVisible();
    await expect(titles(page)).toHaveText(['Song 000', 'Song 002']);
    await action(page, 'Delete playlist');
    await manager(page).getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(manager(page).getByLabel('Choose playlist').locator('option')).toHaveText(['Listening list · 0 songs', 'Late night · 2 songs']);
    await page.reload();
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await expect(library(page).getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await openLists(page);
    await manager(page).getByLabel('Choose playlist').selectOption({ label: 'Late night · 2 songs' });
    await expect(titles(page)).toHaveText(['Song 000', 'Song 002']);
});

test('playlist sorts and searches the entire collection before pagination and plays the full queue', async ({ page }) => {
    await seeded(page, 150, true);
    await chooseNamed(page);
    await expect(titles(page).first()).toHaveText('Song 149');
    await manager(page).getByLabel('Sort playlist').selectOption('Title');
    await expect(titles(page).first()).toHaveText('Song 000');
    await expectLoadedTracks(page, 100);
    await manager(page).getByLabel('Sort playlist').selectOption('Newest');
    await expect(titles(page).first()).toHaveText('Song 149');
    await manager(page).getByLabel('Sort playlist').selectOption('Custom');
    await library(page).locator('#music-search').fill('song 001');
    await library(page).getByRole('button', { name: 'Search', exact: true }).click();
    await expect(titles(page)).toHaveText(['Song 001']);
    await library(page).locator('#music-search').fill('');
    await library(page).getByRole('button', { name: 'Search', exact: true }).click();
    await expectLoadedTracks(page, 100);
    await library(page).getByRole('button', { name: 'Play playlist', exact: true }).click();
    const playing = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(playing.getByRole('status')).toContainText('Streaming');
    await expect(playing.locator('.music-player-info strong')).toHaveText('Song 149');
    // The only other playable song is outside the first 100 rendered rows.
    await playing.getByRole('button', { name: 'Next track', exact: true }).click();
    await expect(playing.locator('.music-player-info strong')).toHaveText('Song 000');
    await expect(playing.getByRole('status')).toContainText('Streaming');
    await stopPlayback(page);
    await library(page).getByRole('button', { name: 'Shuffle play', exact: true }).click();
    await expect(playing.getByRole('status')).toContainText('Streaming');
    await expect(playing.locator('.music-player-info strong')).toHaveText(/Song (000|149)/);
    await stopPlayback(page);
    await page.evaluate(async p => {
        const lib = await import(p);
        const all = JSON.parse(await lib.searchLibrary(700, '', 0, 1000));
        const source = JSON.parse(await lib.readSources(700))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify(all.map(t => ({ ...t,
            origin: { ...t.origin, protected: t.id !== playlistIds[0] } }))));
    }, modulePath);
    await openLibrary(page);
    await chooseNamed(page);
    await expectLoadedTracks(page, 100);
    await library(page).getByRole('button', { name: 'Play playlist', exact: true }).click();
    await expect(playing.locator('.music-player-info strong')).toHaveText('Song 000');
    await expect(playing.getByRole('status')).toContainText('Streaming');
    await stopPlayback(page);
});

test('bulk removal affects only its playlist and duplicate additions are ignored', async ({ page }) => {
    await seeded(page, 4, true);
    await clickTrackAction(library(page).locator('.track').first(), 'Add to another playlist');
    await expect(picker(page).getByRole('button', { name: /Evening collection/ })).toBeDisabled();
    await page.keyboard.press('Escape');
    await chooseNamed(page);
    await library(page).getByRole('button', { name: 'Organize tracks', exact: true }).click();
    await library(page).getByLabel('Select Song 003', { exact: true }).check();
    await library(page).getByLabel('Select Song 001', { exact: true }).check();
    await library(page).getByRole('button', { name: 'Remove selected', exact: true }).click();
    await expect(titles(page)).toHaveText(['Song 002', 'Song 000']);
    await action(page, 'Clear tracks');
    await manager(page).getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(titles(page)).toHaveCount(2);
    await action(page, 'Clear tracks');
    await manager(page).getByRole('button', { name: 'Remove all tracks', exact: true }).click();
    await expect(titles(page)).toHaveCount(0);
    await library(page).getByRole('button', { name: 'All tracks', exact: true }).click();
    await expect(titles(page)).toHaveCount(4);
});

test('failed playlist saves retain the editor and durable membership without reporting success', async ({ page }) => {
    await seeded(page, 4, true);
    await chooseNamed(page);
    await page.evaluate(() => {
        const put = IDBObjectStore.prototype.put;
        globalThis.restorePlaylistWrites = () => { IDBObjectStore.prototype.put = put; };
        IDBObjectStore.prototype.put = function (value, ...args) {
            if (this.name === 'state' && value.key === 'playlists') throw new DOMException('Synthetic playlist storage full', 'QuotaExceededError');
            return put.call(this, value, ...args);
        };
    });
    await action(page, 'Rename');
    await manager(page).getByLabel('Playlist name', { exact: true }).fill('Unsaved title');
    await manager(page).getByRole('button', { name: 'Save playlist', exact: true }).click();
    await expect(manager(page).getByRole('alert')).toContainText('Synthetic playlist storage full');
    await expect(library(page).getByRole('heading', { name: 'Evening collection', exact: true })).toBeVisible();
    await expect(manager(page).getByLabel('Playlist name')).toHaveValue('Unsaved title');
    await expect(manager(page).getByRole('status')).toHaveCount(0);
    await page.evaluate(() => restorePlaylistWrites());
    await manager(page).getByRole('button', { name: 'Save playlist', exact: true }).click();
    await expect(library(page).getByRole('heading', { name: 'Unsaved title', exact: true })).toBeVisible();
});

test('browser source cleanup prunes all named playlists for the selected account', async ({ page }) => {
    await seeded(page, 4, true);
    const result = await page.evaluate(async p => {
        const lib = await import(p);
        const named = JSON.parse(await lib.readState(700, 'playlists'));
        await lib.saveState(701, 'playlists', JSON.stringify(named));
        const removed = JSON.parse(await lib.removeSource(700, -100123));
        return { removed: removed.length, lists: JSON.parse(await lib.readState(700, 'playlists')), other: JSON.parse(await lib.readState(701, 'playlists')) };
    }, modulePath);
    expect(result.removed).toBe(4);
    expect(result.lists[0].Tracks).toEqual([]);
    expect(result.other[0].Tracks).toHaveLength(4);
});

test('sign-out erases named playlists for its account and leaves other account state intact', async ({ page }) => {
    await seeded(page, 4, true);
    await page.evaluate(async p => {
        const lib = await import(p);
        await lib.saveState(701, 'playlists', await lib.readState(700, 'playlists'));
    }, modulePath);
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByText('Signed out remotely and erased local account data. Reopen ChannelWave to connect.', { exact: true })).toBeVisible();
    const result = await page.evaluate(async p => {
        const lib = await import(p);
        return { erased: await lib.readState(700, 'playlists'), other: JSON.parse(await lib.readState(701, 'playlists')) };
    }, modulePath);
    expect(result.erased).toBeNull();
    expect(result.other[0].Tracks).toHaveLength(4);
    await page.reload();
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await openLibrary(page);
    await openLists(page);
    await expect(manager(page).getByLabel('Choose playlist').locator('option')).toHaveText(['Listening list · 0 songs']);
});

for (const width of [1440, 390, 320, 820]) {
    test(`playlist management and selection fit ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 });
        await seeded(page, 4, true);
        await chooseNamed(page);
        await library(page).getByRole('button', { name: 'Organize tracks', exact: true }).click();
        await library(page).getByLabel('Select Song 003', { exact: true }).check();
        await expect(library(page).getByRole('button', { name: 'Move Song 003 up', exact: true })).toBeDisabled();
        await expect(library(page).getByRole('button', { name: 'Move Song 003 down', exact: true })).toBeEnabled();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.emulateMedia({ reducedMotion: 'reduce' });
        await fs.mkdir('artifacts/playlists', { recursive: true });
        await page.locator('#main-content').evaluate(el => { el.scrollTop = 0; });
        await page.screenshot({ path: `artifacts/playlists/management-${width}.png`, fullPage: true });
        await library(page).locator('.track').first().scrollIntoViewIfNeeded();
        if (width === 390) {
            const selection = library(page).getByLabel('Select Song 003', { exact: true });
            await selection.scrollIntoViewIfNeeded();
            await expect(selection).toBeInViewport({ ratio: 1 });
            const checkbox = await selection.boundingBox();
            const dock = await page.locator('.idle-player').boundingBox();
            expect(checkbox.y + checkbox.height).toBeLessThanOrEqual(dock.y);
        }
        await expect(library(page).getByRole('button', { name: 'Move Song 003 down', exact: true })).toBeInViewport();
        await page.screenshot({ path: `artifacts/playlists/rows-${width}.png`, fullPage: true });
        await action(page, 'Rename');
        await manager(page).getByLabel('Playlist name').fill('آهنگ‌های شبانه · Evening collection');
        await page.screenshot({ path: `artifacts/playlists/editor-${width}.png`, fullPage: true });
        await manager(page).getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(manager(page).getByLabel('Choose playlist')).toBeFocused();
        await library(page).getByRole('button', { name: 'Add selected to playlist', exact: true }).click();
        await expect(picker(page)).toBeVisible();
        await page.screenshot({ path: `artifacts/playlists/picker-${width}.png`, fullPage: true });
        await page.keyboard.press('Escape');
    });
}
