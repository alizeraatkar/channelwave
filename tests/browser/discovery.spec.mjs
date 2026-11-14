import { openRoot, openCategory } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, openLibrary } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const main = page => page.locator('.music-content');
async function seed(page, count = 12) {
    await installTelegramFixture(page, 'progressive');
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled({ timeout: 20000 });
    const bytes = (await fs.stat(new URL('./fixtures/stream-tone.mp3', import.meta.url))).size;
    await page.evaluate(async ({ modulePath, count, bytes }) => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -100123, name: 'Café Sessions · موسیقی', historyComplete: true };
        const tracks = Array.from({ length: count }, (_, i) => ({
            id: crypto.randomUUID(), identity: `700:-100123:${String(i + 1).padStart(6, '0')}`,
            origin: { accountId: 700, chatId: source.chatId, messageId: i + 1, sourceName: source.name, protected: false },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.mp3', mimeType: 'audio/mpeg', size: bytes },
            originalTitle: `Song ${String(i + 1).padStart(5, '0')}`, originalPerformer: i % 2 ? 'هنرمند & Artist' : 'Café Artist',
            title: `Song ${String(i + 1).padStart(5, '0')}`, performer: i % 2 ? 'هنرمند & Artist' : 'Café Artist',
            duration: i === count - 1 ? 0 : i + 10, publishedAt: new Date(Date.UTC(2026, 0, i + 1)).toISOString(),
            available: true, favorite: i % 2 === 0, embeddedMetadata: { album: 'Night Sessions', track: String(count - i), disc: '1' }
        }));
        for (let offset = 0; offset < count; offset += 1000) await lib.savePage(JSON.stringify(source), JSON.stringify(tracks.slice(offset, offset + 1000)));
        globalThis.discoveryTracks = tracks;
        const start = Date.now() - 60000;
        const events = [0, 1, 0].map((index, i) => ({ id: crypto.randomUUID(), account: 700, entryId: null,
            track: { id: tracks[index].id, account: 700, chat: source.chatId, message: index + 1, mediaKey: 'fixture-unique:' + bytes,
                title: tracks[index].title, artist: tracks[index].performer, artistKey: 'artist', source: source.name, duration: tracks[index].duration },
            started: start + i * 10000, lastActive: start + i * 10000 + 1000, activeMilliseconds: 1000, thresholdMilliseconds: 30000,
            qualifiedAt: null, startPosition: 0, endPosition: 1, outcome: 'changed', spans: [], offline: false, resumed: false, hasGaps: false, ruleVersion: 1 }));
        await lib.commitListening(JSON.stringify({ commit: { account: 700, expectedRevision: 0, generation: 0,
            preferences: { collect: true, retentionDays: 0, rememberPlayer: true }, player: null, events, forgetPlayer: false }, buckets: [] }));
    }, { modulePath, count, bytes });
    await connectFixture(page); await openLibrary(page);
    await expect(main(page).getByRole('combobox', { name: 'Sort tracks', exact: true })).toBeVisible();
}
async function home(page) { await openRoot(page, 'Home'); await expect(page.getByRole('region', { name: 'New', exact: true })).toBeVisible(); }
async function retainSession(page) {
    await page.evaluate(() => new Promise((resolve, reject) => {
        const request = indexedDB.open('/channelwave-telegram/dbfs', 1);
        request.onsuccess = () => { request.result.close(); resolve(); }; request.onerror = () => reject(request.error);
    }));
}

test('sorts the full catalog and keeps unknown duration last', async ({ page }) => {
    await seed(page, 1105);
    await main(page).getByRole('combobox', { name: 'Sort tracks', exact: true }).selectOption('9');
    await expect(page.locator('.music-track-title strong').first()).toHaveText('Song 01104');
    await main(page).getByRole('combobox', { name: 'Sort tracks', exact: true }).selectOption('3');
    await expect(page.locator('.music-track-title strong').first()).toHaveText('Song 01105');
    await main(page).getByRole('combobox', { name: 'Sort tracks', exact: true }).selectOption('2');
    await expect(page.locator('.music-track-title strong').first()).toHaveText('Song 00001');
    expect(await page.locator('.music-track').count()).toBeLessThan(60);
});

for (const width of [390, 1440]) {
    test(`artist and album text links navigate without opening a player or interrupting playback at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 }); await seed(page);
        const row = () => page.locator('.music-track').filter({ has: page.getByRole('button', { name: 'View artwork and player for Song 00001', exact: true }) });
        await row().getByRole('link', { name: 'Browse artist Café Artist', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
        await expect(page.locator('.music-count')).toContainText('6 tracks');
        await expect(page.getByRole('dialog', { name: 'Music player', exact: true })).toHaveCount(0);
        expect(await page.evaluate(() => !!globalThis.fixtureAudio)).toBe(false);
        await page.goBack();
        await expect(page.getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
        await row().getByRole('button', { name: 'Stream', exact: true }).click();
        const dock = page.getByRole('region', { name: 'Now playing', exact: true });
        await expect(dock.getByRole('button', { name: 'Pause playback', exact: true })).toBeVisible();
        await page.evaluate(() => { globalThis.creditLinkAudio = fixtureAudio; });
        await row().getByRole('link', { name: 'Browse album Night Sessions', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Night Sessions', exact: true })).toBeVisible();
        await expect(page.locator('.music-count')).toContainText('6 tracks');
        await expect(dock.locator('.music-player-info strong')).toHaveText('Song 00001');
        expect(await page.evaluate(() => fixtureAudio === creditLinkAudio && !fixtureAudio.paused)).toBe(true);
        await dock.getByRole('link', { name: 'Browse artist Café Artist', exact: true }).focus();
        await page.keyboard.press('Enter');
        await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
        await dock.getByRole('button', { name: 'Open full-screen player', exact: true }).click();
        const player = page.getByRole('dialog', { name: 'Music player', exact: true });
        await player.getByRole('link', { name: 'Browse album Night Sessions', exact: true }).click();
        await expect(player).toHaveCount(0);
        await expect(page.getByRole('heading', { name: 'Night Sessions', exact: true })).toBeVisible();
        expect(await page.evaluate(() => fixtureAudio === creditLinkAudio && !fixtureAudio.paused)).toBe(true);
        await fs.mkdir('artifacts/credit-links', { recursive: true });
        await page.screenshot({ path: `artifacts/credit-links/album-${width}.png` });
        await home(page);
        await page.getByRole('region', { name: 'Recently Played', exact: true }).getByRole('link', { name: 'Browse artist Café Artist', exact: true }).first().click();
        await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
        await openCategory(page, 'Albums');
        await expect(page.locator('.discovery-group')).toHaveCount(2);
        await page.locator('.discovery-group').getByRole('link', { name: 'Browse artist Café Artist', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
        expect(await page.evaluate(() => fixtureAudio === creditLinkAudio && !fixtureAudio.paused)).toBe(true);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    });
}

test('credit links close queue and details while unknown credits remain plain text', async ({ page }) => {
    await seed(page);
    const first = page.locator('.music-track').filter({ has: page.getByRole('button', { name: 'View artwork and player for Song 00001', exact: true }) });
    await first.getByRole('button', { name: 'Stream', exact: true }).click();
    const dock = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(dock.getByRole('button', { name: 'Pause playback', exact: true })).toBeVisible();
    await dock.getByRole('button', { name: 'Open Up next', exact: true }).click();
    const queue = page.getByRole('dialog', { name: 'Up next', exact: true });
    await queue.getByRole('region', { name: 'Current queue song', exact: true }).getByRole('link', { name: 'Browse artist Café Artist', exact: true }).click();
    await expect(queue).not.toBeVisible();
    await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
    await first.getByRole('button', { name: 'More actions', exact: true }).click();
    await page.getByRole('dialog', { name: 'Song actions', exact: true }).getByRole('button', { name: 'Edit details', exact: true }).click();
    const details = page.getByRole('dialog', { name: 'Edit details', exact: true });
    await details.getByRole('link', { name: 'Browse album Night Sessions', exact: true }).click();
    await expect(details).not.toBeVisible();
    await expect(page.getByRole('heading', { name: 'Night Sessions', exact: true })).toBeVisible();
    expect(await page.evaluate(() => !fixtureAudio.paused)).toBe(true);
    await page.evaluate(async path => {
        const lib = await import(path), source = JSON.parse(await lib.readSource(700, -100123));
        const track = { ...discoveryTracks[0], id: crypto.randomUUID(), identity: '700:-100123:999',
            origin: { ...discoveryTracks[0].origin, messageId: 999 }, title: 'Uncredited song', originalTitle: 'Uncredited song',
            originalPerformer: '', performer: 'Unknown artist', embeddedMetadata: { album: 'No artist album' } };
        await lib.savePage(JSON.stringify(source), JSON.stringify([track]));
    }, modulePath);
    await page.locator('.music-tabs').getByRole('button', { name: 'All tracks', exact: true }).click();
    const uncredited = page.locator('.music-track').filter({ hasText: 'Uncredited song' });
    await expect(uncredited).toContainText('Unknown artist');
    await expect(uncredited.getByRole('link')).toHaveCount(0);
    expect(await page.locator('button a, a button').count()).toBe(0);
});

test('artist and album links browse saved tracks while disconnected', async ({ page, context }) => {
    await seed(page);
    const first = page.locator('.music-track').filter({ has: page.getByRole('button', { name: 'View artwork and player for Song 00001', exact: true }) });
    await first.getByRole('button', { name: 'More actions', exact: true }).click();
    await page.getByRole('dialog', { name: 'Song actions', exact: true }).getByRole('button', { name: 'Save offline', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof globalThis.completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    await expect.poll(() => page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)).length, modulePath)).toBe(1);
    await context.setOffline(true); await page.reload();
    await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
    await page.locator('.music-track').getByRole('link', { name: 'Browse artist Café Artist', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
    await expect(page.locator('.music-count')).toContainText('1 track');
    await page.locator('.music-track').getByRole('link', { name: 'Browse album Night Sessions', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Night Sessions', exact: true })).toBeVisible();
    await page.locator('.music-track').getByRole('button', { name: 'Play', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Now playing', exact: true }).getByRole('status')).toContainText('Playing offline');
});

test('artist and album groups filter all tracks and restore through Back and reload', async ({ page }) => {
    await seed(page); await retainSession(page);
    const tabs = page.getByRole('group', { name: 'Browse your library', exact: true });
    await openCategory(page, 'Artists');
    await expect(page.locator('.discovery-group')).toHaveCount(2);
    await page.locator('.discovery-group').filter({ hasText: 'Café Artist' }).locator('.discovery-group-open').click();
    await expect(page.getByRole('heading', { name: 'Café Artist', exact: true })).toBeVisible();
    await expect(page.locator('.music-track')).toHaveCount(6);
    await page.goBack(); await expect(page.locator('.discovery-group')).toHaveCount(2);
    await openCategory(page, 'Albums');
    await expect(page.locator('.discovery-group')).toHaveCount(2);
    await page.locator('.discovery-group').filter({ hasText: 'Café Artist' }).locator('.discovery-group-open').click();
    await expect(page.locator('.music-track-title strong').first()).toHaveText('Song 00011');
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Night Sessions', exact: true })).toBeVisible({ timeout: 30000 });
    await expect(main(page).getByRole('combobox', { name: 'Sort tracks', exact: true })).toHaveValue('10');
    await expect(page.locator('.music-track-title strong').first()).toHaveText('Song 00011');
});

test('combined liked and offline filters are staged and empty saved scope is honest', async ({ page }) => {
    await seed(page);
    await page.getByRole('button', { name: 'Filters (0)', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Filters', exact: true });
    await dialog.getByRole('checkbox', { name: 'Liked only', exact: true }).check();
    await dialog.getByRole('button', { name: 'Show results', exact: true }).click();
    await expect(page.locator('.music-track')).toHaveCount(6);
    await page.getByRole('button', { name: 'Filters (1)', exact: true }).click();
    await dialog.getByRole('checkbox', { name: 'Saved offline', exact: true }).check();
    await dialog.getByRole('button', { name: 'Show results', exact: true }).click();
    await expect(page.locator('.music-track')).toHaveCount(0);
    await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
    await expect(page.locator('.music-count')).toContainText('12 tracks');
});

test('missing groups and malformed route filters recover without breaking the library', async ({ page }) => {
    await seed(page); await retainSession(page);
    const route = state => '/#view=library&browse=' + encodeURIComponent(encodeURIComponent(JSON.stringify(state)));
    await page.goto(route({ Mode: 'artists', Group: 'removed-credit', Label: 'Removed artist', Order: 2,
        Criteria: { Text: null, Title: null, Artist: null }, Source: null }));
    await expect(page.getByRole('heading', { name: 'Removed artist', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'No matches just yet', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Show all tracks', exact: true }).click();
    await expect(page.locator('.music-count')).toContainText('12 tracks');
    await page.goto(route({ Mode: 'tracks', Order: 0, Criteria: { MinDuration: 100, MaxDuration: 1 } }));
    await expect(page.locator('.music-count')).toContainText('12 tracks');
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('Home uses distinct partial listens and browsing never starts playback', async ({ page }) => {
    await seed(page); await home(page);
    const recent = page.getByRole('region', { name: 'Recently Played', exact: true });
    await expect(recent.locator('.home-track')).toHaveCount(2);
    await expect(recent.locator('strong').first()).toHaveText('Song 00001');
    await expect(page.locator('.home-feature')).toHaveCount(0);
    expect(await page.evaluate(() => globalThis.fixtureAudio?.paused ?? true)).toBe(true);
    await recent.getByRole('button', { name: 'View artwork and player for Song 00001', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Music player', exact: true })).toBeVisible();
    expect(await page.evaluate(() => globalThis.fixtureAudio?.paused ?? true)).toBe(true);
});

test('browser projection ordering groups and account isolation agree across queries', async ({ page }) => {
    await seed(page);
    const result = await page.evaluate(async path => {
        const lib = await import(path), tracks = globalThis.discoveryTracks;
        const request = { criteria: { text: '', title: '', artist: '', minDuration: null, maxDuration: null, publishedFrom: null, publishedBefore: null },
            favorites: false, chat: null, playlist: null, sort: 0, offset: 0, limit: 100, terms: [], titleTerms: [], artistTerms: [], order: 9 };
        const rows = JSON.parse(await lib.searchLibraryAdvanced(700, JSON.stringify(request)));
        const queue = JSON.parse(await lib.searchLibraryAdvanced(700, JSON.stringify({ ...request, idsOnly: true })));
        const queueSame = JSON.stringify(queue) === JSON.stringify(rows.map(row => row.id));
        const groups = JSON.parse(await lib.searchLibraryAdvanced(700, JSON.stringify({ ...request, groups: 'albums' })));
        const other = JSON.parse(await lib.searchLibraryAdvanced(701, JSON.stringify(request)));
        const recent = JSON.parse(await lib.recentListeningTracks(700, 6));
        const baseline = await lib.readState(700, 'library-added-baseline-v1');
        const before = JSON.parse(await lib.readTrack(700, tracks[0].id));
        await lib.savePage(JSON.stringify({ accountId: 700, chatId: -100123, name: 'Renamed' }), JSON.stringify([tracks[0]]));
        const after = JSON.parse(await lib.readTrack(700, tracks[0].id));
        return { queueSame, first: rows[0].title, last: rows.at(-1).duration, groups: groups.length, count: groups.reduce((sum, g) => sum + g.count, 0), other: other.length, recent: recent.length,
            preserved: before.addedAt === after.addedAt, baseline: baseline === await lib.readState(700, 'library-added-baseline-v1') };
    }, modulePath);
    expect(result).toEqual({ queueSame: true, first: 'Song 00011', last: 0, groups: 2, count: 12, other: 0, recent: 2, preserved: true, baseline: true });
});

test('Home resumes one saved session and track menus remain available', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 1000 });
    await seed(page); await retainSession(page); await home(page);
    const recent = page.getByRole('region', { name: 'Recently Played', exact: true });
    await recent.getByRole('button', { name: 'More actions', exact: true }).first().click();
    await page.getByRole('dialog', { name: 'Song actions', exact: true }).getByRole('button', { name: 'Add to another playlist', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Add to playlist', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    await recent.getByRole('button', { name: 'Play', exact: true }).first().click();
    const resume = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(resume.getByRole('button', { name: 'Pause playback', exact: true })).toBeVisible();
    await page.evaluate(() => {
        globalThis.homeRefreshFlashes = 0;
        globalThis.homeRefreshObserver = new MutationObserver(() => {
            if (document.querySelector('.home-recent') && document.body.textContent.includes('Loading recent music…')) homeRefreshFlashes++;
        });
        homeRefreshObserver.observe(document.querySelector('main'), { childList: true, subtree: true });
    });
    // Observe a real periodic listening commit, rather than sleeping for a checkpoint interval.
    const revision = await page.evaluate(async path => JSON.parse(await (await import(path)).readListeningState(700)).revision, modulePath);
    await expect.poll(() => page.evaluate(async path => JSON.parse(await (await import(path)).readListeningState(700)).revision, modulePath), { timeout: 12000 }).toBeGreaterThan(revision);
    expect(await page.evaluate(() => { homeRefreshObserver.disconnect(); return homeRefreshFlashes; })).toBe(0);
    await expect.poll(() => page.evaluate(() => globalThis.fixtureAudio?.currentTime ?? 0)).toBeGreaterThan(1);
    await resume.getByRole('button', { name: 'Pause playback', exact: true }).click();
    await expect(resume.getByRole('button', { name: 'Resume playback', exact: true })).toBeVisible();
    await expect(resume.locator('.music-player-info')).toContainText('Song 00001');
    await fs.mkdir('artifacts/discovery', { recursive: true });
    await page.screenshot({ path: 'artifacts/discovery/continue-390.png' });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.screenshot({ path: 'artifacts/discovery/continue-1440.png' });
    await page.reload();
    await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toBeVisible({ timeout: 30000 });
    await expect(page.getByRole('region', { name: 'Now playing', exact: true }).getByRole('button', { name: 'Resume playback', exact: true })).toBeVisible();
    expect(await page.evaluate(() => globalThis.fixtureAudio?.paused ?? true)).toBe(true);
});

test('existing library import label and baseline survive reload', async ({ page }) => {
    await seed(page); await retainSession(page);
    await page.evaluate(async path => {
        const lib = await import(path); await lib.openLibrary(); const db = await new Promise((resolve, reject) => { const request = indexedDB.open('channelwave-library'); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
        await new Promise((resolve, reject) => {
            const tx = db.transaction('tracks', 'readwrite'), request = tx.objectStore('tracks').openCursor();
            request.onsuccess = () => { const cursor = request.result; if (!cursor) return; const row = cursor.value; delete row.addedAt; cursor.update(row); cursor.continue(); };
            tx.oncomplete = resolve; tx.onerror = () => reject(tx.error);
        });
        db.close(); lib.clearSearchCache();
    }, modulePath);
    await home(page);
    await expect(page.getByRole('region', { name: 'New', exact: true })).toContainText('Song 00001');
    const before = await page.evaluate(async path => (await import(path)).readState(700, 'library-added-baseline-v1'), modulePath);
    await page.reload();
    await expect(page.getByRole('region', { name: 'New', exact: true })).toContainText('Song 00001');
    const after = await page.evaluate(async path => (await import(path)).readState(700, 'library-added-baseline-v1'), modulePath);
    expect(after).toBe(before);
});

test('responsive Home and browsing captures with bounded 10000-track rendering', async ({ page }) => {
    test.setTimeout(120000);
    await seed(page, 10000); await fs.mkdir('artifacts/discovery', { recursive: true });
    for (const width of [320, 390, 430, 820, 1100, 1440, 1920]) {
        await page.setViewportSize({ width, height: 1000 }); await home(page);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        if ([390, 1440].includes(width)) await page.screenshot({ path: `artifacts/discovery/home-${width}.png` });
        await openLibrary(page);
        await openCategory(page, 'Albums');
        await expect(page.locator('.discovery-group')).toHaveCount(2);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        if ([390, 1440].includes(width)) await page.screenshot({ path: `artifacts/discovery/albums-${width}.png` });
        await openCategory(page, 'Tracks');
        expect(await page.locator('.music-track').count()).toBeLessThan(65);
        if ([390, 1440].includes(width)) await page.screenshot({ path: `artifacts/discovery/tracks-${width}.png` });
    }
});
