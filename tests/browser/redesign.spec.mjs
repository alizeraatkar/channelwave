import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { seed } from './fixtures/redesign.mjs';
import { openRoot, openLibrary, clickTrackAction } from './fixtures/telegram.mjs';

const bridge = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const content = page => page.locator('#main-content');
async function retainSession(page) {
    await page.evaluate(() => new Promise((resolve, reject) => {
        const request = indexedDB.open('/channelwave-telegram/dbfs', 1);
        request.onsuccess = () => { request.result.close(); resolve(); }; request.onerror = () => reject(request.error);
    }));
}

for (const width of [320, 390, 820, 1440]) {
    test(`redesign roots, focused categories and bounded previews at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 }); await seed(page, 1205);
        await openRoot(page, 'Library');
        await expect(page.locator('.library-tiles').first().locator('strong')).toHaveText(['Tracks', 'Artists', 'Albums', 'Playlists', 'Offline', 'Listening activity', 'Sources']);
        await expect(page.getByRole('button', { name: 'Tracks 1205 tracks' })).toBeVisible();
        await expect(content(page).locator('input[type=search]:visible')).toHaveCount(0);
        await fs.mkdir('artifacts/redesign', { recursive: true });
        await page.screenshot({ path: `artifacts/redesign/library-${width}.png` });
        await page.getByRole('button', { name: /^Artists \d/ }).click();
        await expect(page.locator('.discovery-group')).toHaveCount(2);
        await expect(page.getByRole('navigation', { name: 'Mobile navigation' })).toHaveCount(0);
        if (width > 760) await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
        await page.getByRole('button', { name: 'Open artist Café Artist', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
        await page.getByRole('button', { name: 'Go back', exact: true }).click();
        await expect(page.locator('.discovery-group')).toHaveCount(2);
        await openRoot(page, 'Home');
        await expect(page.getByRole('tab')).toHaveText(['All', 'Recently Played', 'New', 'Playlists', 'Liked']);
        const fresh = page.getByRole('region', { name: 'New', exact: true });
        await expect(fresh.locator('.home-track')).toHaveCount(width <= 760 ? 2 : 6);
        expect(await page.evaluate(() => !!globalThis.fixtureAudio)).toBe(false);
        await page.screenshot({ path: `artifacts/redesign/home-${width}.png` });
        await fresh.getByRole('button', { name: 'See all' }).click();
        await expect(page.getByRole('tab', { name: 'New', exact: true })).toHaveAttribute('aria-selected', 'true');
        await expect(page.locator('.home-track').first()).toBeVisible();
        expect(await page.locator('.home-track').count()).toBeLessThan(65);
        await page.screenshot({ path: `artifacts/redesign/new-${width}.png` });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await openRoot(page, 'Settings');
        await expect(page.getByRole('heading', { name: 'Music details' })).toBeVisible();
        await openRoot(page, 'Profile');
        await expect(page.getByRole('button', { name: 'Sign out and erase local data' })).toBeVisible();
        await page.screenshot({ path: `artifacts/redesign/profile-${width}.png` });
    });
}

test('search modes, saved filter shortcuts and independent display preferences persist', async ({ page }) => {
    await seed(page); await retainSession(page); await openRoot(page, 'Library');
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await expect(page.locator('#music-search')).toBeFocused();
    const modes = page.getByRole('group', { name: 'Search result types' });
    await page.locator('#music-search').fill('Artist Café');
    await modes.getByRole('button', { name: 'Artists', exact: true }).click();
    await expect(page.locator('.discovery-group')).toHaveCount(1);
    await expect(page.locator('.discovery-group')).toContainText('Café Artist');
    await modes.getByRole('button', { name: 'Tracks', exact: true }).click();
    await page.locator('#music-search').fill('"Café Artist"');
    await expect(page.locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', '6');
    await page.locator('.collection-actions').getByRole('button', { name: 'Grid view' }).click();
    await page.locator('.collection-actions').getByRole('button', { name: 'Save filter', exact: true }).click();
    await page.getByLabel('Filter name', { exact: true }).fill('Café collection');
    await page.getByRole('dialog', { name: 'Save filter', exact: true }).getByRole('button', { name: 'Save filter', exact: true }).click();
    await openRoot(page, 'Library');
    const tile = page.locator('.saved-filter-tile');
    await tile.locator('summary').click();
    await tile.getByRole('button', { name: 'Add to Home tabs' }).click();
    await tile.getByRole('button', { name: 'Remove tile' }).click();
    await expect(tile).toHaveCount(0);
    await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'Café collection', exact: true }).click();
    await expect(page.locator('.home-track')).toHaveCount(6);
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Grid view', exact: true }).first()).toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('tab', { name: 'Café collection', exact: true }).click();
    await expect(page.getByRole('button', { name: 'List view', exact: true }).first()).toHaveAttribute('aria-pressed', 'true');
    await page.reload();
    await expect(page.getByRole('tab', { name: 'All', exact: true })).toHaveAttribute('aria-selected', 'true');
    await page.getByRole('tab', { name: 'Café collection', exact: true }).click();
    await expect(page.getByRole('button', { name: 'List view', exact: true }).first()).toHaveAttribute('aria-pressed', 'true');
    await openRoot(page, 'Library');
    await page.getByRole('button', { name: 'Save a filter', exact: true }).click();
    await page.getByRole('dialog', { name: 'Save a filter' }).getByRole('button', { name: 'Café collection', exact: true }).click();
    await expect(tile).toContainText('Café collection');
});

test('pin all target types, prevent duplicate pins, reorder and remove shortcuts', async ({ page }) => {
    await seed(page); await retainSession(page); await openRoot(page, 'Home');
    await page.getByRole('button', { name: 'Edit Home tabs', exact: true }).click();
    for (const [kind, label] of [['Artist', 'Café Artist'], ['Album', 'Night Sessions'], ['Playlist', 'Listening list'], ['Track', 'Song 00001']]) {
        await page.getByRole('button', { name: 'Add from Library', exact: true }).click();
        const picker = page.getByRole('dialog', { name: 'Add from Library', exact: true });
        await picker.getByLabel('Item type').selectOption(kind);
        await picker.getByRole('button', { name: label, exact: true }).first().click();
    }
    await page.getByRole('button', { name: 'Move New up', exact: true }).click();
    await expect(page.locator('.tab-editor-label strong').nth(1)).toHaveText('New');
    await page.getByRole('button', { name: 'Add from Library', exact: true }).click();
    const picker = page.getByRole('dialog', { name: 'Add from Library', exact: true });
    await picker.getByLabel('Item type').selectOption('Artist');
    await expect(picker.getByRole('button', { name: 'Café Artist', exact: true })).toBeDisabled();
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Remove Song 00001 tab', exact: true }).click();
    await expect(page.locator('.tab-editor-row')).toHaveCount(8);
    await page.reload();
    await expect(page.locator('.tab-editor-row')).toHaveCount(8);
    await expect(page.locator('.tab-editor-label strong').nth(1)).toHaveText('New');
    await page.screenshot({ path: 'artifacts/redesign/customize.png' });
});

test('player survives focused navigation, Android Back closes dialogs before leaving pages', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 1000 }); await seed(page); await retainSession(page);
    await page.locator('.music-track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('button', { name: 'Pause playback' })).toBeVisible();
    await page.evaluate(() => { globalThis.redesignAudio = fixtureAudio; });
    await openRoot(page, 'Home');
    const nav = page.getByRole('navigation', { name: 'Mobile navigation' });
    const playerBounds = await player.boundingBox();
    expect(playerBounds.y + playerBounds.height).toBeLessThanOrEqual((await nav.boundingBox()).y);
    await openRoot(page, 'Library');
    await page.getByRole('button', { name: /^Tracks \d/ }).click();
    await player.getByRole('button', { name: 'Open full-screen player' }).click();
    expect(await page.evaluate(() => channelwaveHandleBack())).toBe(true);
    await expect(page.getByRole('dialog', { name: 'Music player', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => channelwaveHandleBack())).toBe(true);
    await expect(page.getByRole('heading', { name: 'Library', exact: true })).toBeVisible();
    expect(await page.evaluate(() => channelwaveHandleBack())).toBe(false);
    expect(await page.evaluate(() => fixtureAudio === redesignAudio && !fixtureAudio.paused)).toBe(true);
    await player.getByRole('button', { name: 'Pause playback' }).click();
    await page.reload();
    await expect(player.getByRole('button', { name: 'Resume playback' })).toBeVisible();
    expect(await page.evaluate(() => globalThis.fixtureAudio?.paused ?? true)).toBe(true);
});
