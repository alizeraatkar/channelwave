import { test, expect } from '@playwright/test';
import { seed } from './fixtures/redesign.mjs';
import { openRoot, openCategory } from './fixtures/telegram.mjs';

const bridge = '/_content/ChannelWave.Platform.Browser/channelwave.js';
async function retainSession(page) {
    await page.evaluate(() => new Promise((resolve, reject) => {
        const r = indexedDB.open('/channelwave-telegram/dbfs', 1);
        r.onsuccess = () => { r.result.close(); resolve(); }; r.onerror = () => reject(r.error);
    }));
}

test('recent listens page beyond one hundred distinct tracks and play the complete collection', async ({ page }) => {
    await seed(page, 205); await retainSession(page);
    const result = await page.evaluate(async path => {
        const lib = await import(path), state = JSON.parse(await lib.readListeningState(700));
        const original = JSON.parse(await lib.listeningHistory(700, JSON.stringify({ from: 0, to: Number.MAX_SAFE_INTEGER, search: '', limit: 1 }))).items[0];
        const events = [...discoveryTracks, discoveryTracks[0]].map((track, index) => ({ ...original, id: crypto.randomUUID(),
            track: { ...original.track, id: track.id, title: track.title, artist: track.performer, message: track.origin.messageId },
            started: Date.now() + index * 1000, lastActive: Date.now() + index * 1000 + 500 }));
        await lib.commitListening(JSON.stringify({ commit: { account: 700, expectedRevision: state.revision, generation: state.generation,
            preferences: state.preferences, player: null, events, forgetPlayer: false }, buckets: [] }));
        const pages = await Promise.all([0, 100, 200].map(async offset => JSON.parse(await lib.recentListeningTracks(700, 100, offset))));
        return { lengths: pages.map(p => p.length), distinct: new Set(pages.flat().map(t => t.trackId)).size, first: pages[0][0].trackId, expected: discoveryTracks[0].id };
    }, bridge);
    expect(result.lengths).toEqual([100, 100, 5]); expect(result.distinct).toBe(205); expect(result.first).toBe(result.expected);
    await page.reload(); await openRoot(page, 'Home'); await page.getByRole('tab', { name: 'Recently Played', exact: true }).click();
    await expect(page.locator('.home-collection [data-loaded-count]')).toHaveAttribute('data-loaded-count', '100');
    await page.locator('.home-track').first().getByRole('button', { name: 'Play', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
    await player.getByRole('button', { name: 'Open Up next', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Up next' }).locator('.queue-list-heading')).toContainText('204');
    expect(await page.locator('.home-track').count()).toBeLessThan(80);
});

test('Home keyboard navigation and Back restore tab, view, focus and scroll', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 1000 }); await seed(page, 305); await retainSession(page);
    await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'All', exact: true }).focus();
    await page.keyboard.press('ArrowRight');
    await expect(page.getByRole('tab', { name: 'Recently Played', exact: true })).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('ArrowRight');
    await expect(page.getByRole('tab', { name: 'New', exact: true })).toHaveAttribute('aria-selected', 'true');
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    const main = page.locator('#main-content');
    await main.evaluate(el => el.scrollTop = 1200);
    const credit = page.locator('.home-track').filter({ has: page.getByRole('link', { name: 'Browse artist Café Artist', exact: true }) }).nth(3).getByRole('link').first();
    await credit.scrollIntoViewIfNeeded();
    const scroll = await main.evaluate(el => el.scrollTop);
    await credit.click();
    await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Go back', exact: true }).click();
    await expect(page.getByRole('tab', { name: 'New', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('button', { name: 'List view', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(() => main.evaluate(el => el.scrollTop)).toBeGreaterThan(scroll - 80);
    await expect(page.locator('.home-track a:focus')).toHaveCount(1);
    await page.goForward();
    await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
});

test('real Telegram connection updates and user identity appear in Home and Profile', async ({ page }) => {
    await seed(page); await openRoot(page, 'Home');
    for (const [state, label] of [['connectionStateConnecting', 'Connecting'], ['connectionStateConnectingToProxy', 'Connecting to proxy'], ['connectionStateWaitingForNetwork', 'Waiting for network'], ['connectionStateUpdating', 'Updating']]) {
        await page.evaluate(state => fixtureClient.options.onUpdate({ '@type': 'updateConnectionState', state: { '@type': state } }), state);
        await expect(page.locator('.telegram-activity')).toContainText(label);
        await expect(page.locator('.app-topbar .brand strong')).toHaveText('ChannelWave');
    }
    await page.evaluate(() => {
        fixtureClient.options.onUpdate({ '@type': 'updateConnectionState', state: { '@type': 'connectionStateReady' } });
        fixtureClient.options.onUpdate({ '@type': 'updateUser', user: { '@type': 'user', id: 700, first_name: 'Ali', last_name: 'موسیقی', usernames: { active_usernames: ['ali_music'] } } });
    });
    await expect(page.locator('.app-topbar .brand strong')).toHaveText('ChannelWave');
    await expect(page.locator('.telegram-activity')).toBeEmpty();
    await openRoot(page, 'Profile');
    await expect(page.locator('.profile-identity')).toContainText('Ali موسیقی');
    await expect(page.locator('.profile-identity')).toContainText('@ali_music');
    await expect.poll(() => page.evaluate(async path => JSON.parse(await (await import(path)).readState(700, 'profile-identity-v1')).Username, bridge)).toBe('ali_music');
});

test('deleted playlist filters stay unavailable and playlist search honors List', async ({ page }) => {
    await seed(page); await retainSession(page);
    await page.evaluate(async path => {
        const lib = await import(path), existing = JSON.parse(await lib.readState(700, 'playlist'));
        const id = crypto.randomUUID(), filterId = crypto.randomUUID();
        existing.Tracks = [discoveryTracks[0].id]; await lib.saveState(700, 'playlist', JSON.stringify(existing));
        await lib.saveState(700, 'playlists', JSON.stringify([{ Id: id, Name: 'Disposable playlist', Tracks: [discoveryTracks[1].id] }]));
        await lib.saveState(700, 'library-presentation-v1', JSON.stringify({ Version: 1, Tabs: [], Displays: {}, Filters: [{ Id: filterId, Name: 'Playlist filter', ShowInLibrary: true, Query: { Criteria: { Text: 'Song 00002' }, Chat: -100123, PlaylistId: id, Sort: 2, Offset: 0, Limit: 100 } }] }));
        globalThis.deletedPlaylistId = id;
    }, bridge);
    await page.reload(); await openRoot(page, 'Library');
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await page.getByRole('group', { name: 'Search result types' }).getByRole('button', { name: 'Playlists', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Grid view', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    await expect(page.locator('.playlist-catalog .virtual-grid')).toHaveCount(0);
    await openRoot(page, 'Library');
    await page.getByRole('button', { name: 'Playlist filter Saved filter', exact: true }).click();
    await expect(page.getByLabel('Sort playlist', { exact: true })).toHaveValue('Artist');
    await expect(page.locator('.music-track-title strong')).toHaveText(['Song 00002']);
    await page.reload();
    await expect(page.getByLabel('Sort playlist', { exact: true })).toHaveValue('Artist');
    await expect(page.getByRole('searchbox', { name: 'Search this playlist', exact: true })).toHaveValue('Song 00002');
    await expect(page.getByLabel('Source', { exact: true })).toContainText('Café Sessions');
    await expect(page.locator('.music-track-title strong')).toHaveText(['Song 00002']);
    await page.evaluate(async path => (await import(path)).saveState(700, 'playlists', '[]'), bridge);
    await page.reload();
    await expect(page.getByText('This playlist is no longer available.', { exact: false })).toBeVisible();
    await expect(page.locator('.music-track')).toHaveCount(0);
    await page.getByRole('button', { name: 'Open Library', exact: true }).click();
    await expect(page.locator('.saved-filter-tile')).toContainText('Playlist filter');
});

test('saved queries remain live, missing pins are recoverable, and drag reorder persists', async ({ page }) => {
    await seed(page); await retainSession(page);
    await page.evaluate(async path => {
        const lib = await import(path), id = crypto.randomUUID();
        await lib.saveState(700, 'library-presentation-v1', JSON.stringify({ Version: 1, Displays: {}, Filters: [{ Id: id, Name: 'Live tracks', ShowInLibrary: true, Query: { Criteria: { Text: 'Song' }, Offset: 0, Limit: 100 } }],
            Tabs: [{ Id: 'live', Label: 'Live tracks', Item: { Kind: 4, Key: id, Label: 'Live tracks' } }, { Id: 'missing', Label: 'Removed recording', Item: { Kind: 3, Key: crypto.randomUUID(), Label: 'Removed recording' } }] }));
    }, bridge);
    await page.reload(); await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'Live tracks', exact: true }).click();
    await expect(page.locator('.home-collection [data-loaded-count]')).toHaveAttribute('data-loaded-count', '12');
    await page.evaluate(async path => {
        const lib = await import(path), first = JSON.parse(await lib.searchLibrary(700, '', 0, 1))[0];
        await lib.savePage(JSON.stringify({ accountId: 700, chatId: -100123, name: first.origin.sourceName }), JSON.stringify([{ ...first, id: crypto.randomUUID(), identity: '700:-100123:999', origin: { ...first.origin, messageId: 999 }, title: 'Song newly added', originalTitle: 'Song newly added' }]));
    }, bridge);
    await page.reload();
    await expect(page.getByRole('tab', { name: 'All', exact: true })).toHaveAttribute('aria-selected', 'true');
    await page.getByRole('tab', { name: 'Live tracks', exact: true }).click();
    await expect(page.locator('.home-collection [data-loaded-count]')).toHaveAttribute('data-loaded-count', '13');
    await page.getByRole('tab', { name: 'Removed recording', exact: true }).click();
    await expect(page.getByText('This item is no longer available in your library.')).toBeVisible();
    await page.getByRole('button', { name: 'Remove shortcut', exact: true }).click();
    await expect(page.getByRole('tab', { name: 'Removed recording', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Edit Home tabs', exact: true }).click();
    const rows = page.locator('.tab-editor-row');
    const start = await rows.first().locator('.tab-drag-handle').boundingBox(), end = await rows.nth(2).boundingBox();
    await page.mouse.move(start.x + start.width / 2, start.y + start.height / 2);
    await page.mouse.down(); await page.mouse.move(end.x + end.width / 2, end.y + end.height / 2, { steps: 8 }); await page.mouse.up();
    await expect(rows.nth(2)).toHaveAttribute('data-tab-id', 'live');
    await page.reload(); await expect(rows.nth(2)).toHaveAttribute('data-tab-id', 'live');
});

test('all Library tiles have focused routes and 200 percent layout remains usable', async ({ page }) => {
    await page.setViewportSize({ width: 820, height: 1000 }); await seed(page);
    for (const name of ['Tracks', 'Artists', 'Albums', 'Playlists', 'Offline', 'Listening activity', 'Sources']) {
        await openCategory(page, name);
        await expect(page.getByRole('button', { name: 'Go back', exact: true })).toBeVisible();
        await expect(page.getByRole('navigation', { name: 'Mobile navigation' })).toHaveCount(0);
    }
    await page.setViewportSize({ width: 390, height: 1000 }); await openRoot(page, 'Library');
    await page.evaluate(() => {
        const text = [...document.querySelectorAll('button,input,select,h1,h2,h3,p,label,strong,span,bdi')];
        const sizes = text.map(el => parseFloat(getComputedStyle(el).fontSize));
        text.forEach((el, index) => el.style.fontSize = `${sizes[index] * 2}px`);
    });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByRole('button', { name: 'Open search' }).click();
    await expect(page.locator('#music-search')).toBeFocused();
    await page.screenshot({ path: 'artifacts/redesign/search-200-percent.png' });
});
