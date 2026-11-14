import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { savePreparedExport, installTelegramFixture, openLibrary, openChannels, clickTrackAction, selectChannel, stopPlayback, addFixtureSource, connectFixture } from './fixtures/telegram.mjs';
const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
async function expectStoredTracks(page, count) {
    await expect.poll(() => page.evaluate(async p => JSON.parse(await (await import(p)).searchLibrary(700, '', 0, 1000)).length, modulePath)).toBe(count);
}

test('mixed sources are discovered, selected, filtered, restored and removed without indexing unselected sources', async ({ page }) => {
    await installTelegramFixture(page, 'sync-mixed');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    const picker = page.getByRole('region', { name: 'Choose a source' });
    const catalog = page.getByRole('region', { name: 'Find your sources' });
    await expect(catalog).toContainText('Source list is ready.');
    await expect(picker.locator('.channel-row')).toHaveCount(7);
    await expect(picker).not.toContainText('Secret conversation');
    await expect(picker).not.toContainText('Unknown chat type');
    expect(await page.evaluate(() => fixtureCalls.includes('searchChatMessages') || fixtureCalls.includes('downloadFile'))).toBe(false);
    for (const width of [1280, 820, 390]) {
        await page.setViewportSize({ width, height: 900 });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await picker.screenshot({ path: 'artifacts/mixed-source-picker-' + width + '.png' });
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    const search = picker.getByRole('searchbox', { name: 'Search discovered sources' });
    await search.fill('private conversation');
    await expect(picker.locator('.channel-row')).toHaveCount(1);
    await search.fill('');
    for (const name of ['Listening group', 'Private conversation', 'Saved Messages', 'Music bot']) {
        await addFixtureSource(page, name);
        await expect(picker.getByRole('button', { name: 'Added to library: ' + name, exact: true })).toBeDisabled();
    }
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(3);
    await selectChannel(page, '701');
    await expect(page.locator('.track')).toHaveCount(1);
    await expect(page.locator('.music-source')).toContainText('Private conversation');
    expect(await page.evaluate(() => ({ chats: [...new Set(fixturePages.map(x => x.chat))].sort((a, b) => a - b), downloaded: fixtureCalls.includes('downloadFile') })))
        .toEqual({ chats: [-123, 700, 701, 702], downloaded: false });

    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openChannels(page);
    await expect(catalog).toContainText('Source list is ready.');
    await expect(picker.locator('.channel-row')).toHaveCount(7);
    await expect(picker.getByRole('button', { name: 'Added to library: Private conversation', exact: true })).toBeDisabled();
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
    await picker.getByRole('button', { name: 'Remove from library: Private conversation', exact: true }).click();
    await expect(picker.getByRole('button', { name: 'Add to library: Private conversation', exact: true })).toBeEnabled();
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(2);
    await page.getByLabel('Source', { exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Choose a source', exact: true }).getByRole('radio', { name: 'Private conversation', exact: true })).toHaveCount(0);
});

test('WASM indexes, serializes, reloads and searches Unicode audio from a synthetic :) channel', async ({ page }) => {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);

    await addFixtureSource(page, ':)');
    await openLibrary(page);
    await expect(page.locator('.track strong')).toHaveText('Café 🎵');
    await expect(page.getByRole('alert')).toHaveCount(0);
    await page.getByRole('button', { name: 'Favorite track', exact: true }).click();
    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await expect(page.locator('.track strong')).toHaveText('Café 🎵');
    await expect(page.getByRole('button', { name: 'Remove favorite', exact: true })).toBeVisible();
    for (const query of ['cafe', 'کی اهنگ من', ':)']) {
        await page.getByRole('searchbox', { name: 'Search your indexed library' }).fill('no matching fixture');
        await page.getByRole('button', { name: 'Search', exact: true }).click();
        await expect(page.locator('.track')).toHaveCount(0);
        await page.getByRole('searchbox', { name: 'Search your indexed library' }).fill(query);
        await page.getByRole('button', { name: 'Search', exact: true }).click();
        await expect(page.locator('.track strong')).toHaveText('Café 🎵');
        await expect(page.getByRole('alert')).toHaveCount(0);
    }
});

for (const mode of ['normal', 'denied']) {
    test(`WASM Stream ${mode === 'normal' ? 'plays range chunks with working controls' : 'recovers from autoplay denial with Resume'}`, async ({ page }) => {
        await installTelegramFixture(page, mode);
        await page.goto('/');
        await connectFixture(page);

        await addFixtureSource(page, ':)');
    await openLibrary(page);
        const row = page.locator('.track');
        await row.getByRole('button', { name: 'Stream', exact: true }).click();
        const player = page.getByRole('region', { name: 'Now playing', exact: true });
        if (mode === 'denied') {
            await expect(player.getByRole('alert')).toContainText('Press Resume');
            await row.getByRole('button', { name: 'Resume', exact: true }).click();
        }
        await expect(row.getByRole('status')).toHaveText('Now playing');
        await expect(player.getByRole('alert')).toHaveCount(0);
        expect(await page.evaluate(() => ({ paused: fixtureAudio.paused, source: fixtureAudio.src.includes('/__channelwave_audio/'), decoded: fixtureAudio.duration > 0 })))
            .toEqual({ paused: false, source: true, decoded: true });
        expect(await page.evaluate(() => fixtureDownloads.every(x => x.limit > 0 && x.limit <= 65536 && x.synchronous))).toBe(true);
        await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
        await expect(row.getByRole('status')).toContainText('Paused');
        expect(await page.evaluate(() => fixtureAudio.paused)).toBe(true);
        await player.getByRole('slider', { name: 'Playback position' }).fill('2');
        await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeCloseTo(2, 0);
        await player.getByRole('slider', { name: 'Volume', exact: true }).fill('0.4');
        await expect.poll(() => page.evaluate(() => fixtureAudio.volume)).toBeCloseTo(0.4, 1);
        await stopPlayback(page);
        await expect(player).toHaveCount(0);
        await expect(row.getByRole('button', { name: 'Download', exact: true, includeHidden: true })).toBeEnabled();
    });
}

test('WASM shows download errors and permits retry without starting playback', async ({ page }) => {
    await installTelegramFixture(page, 'failed');
    await page.goto('/');
    await connectFixture(page);

    await addFixtureSource(page, ':)');
    await openLibrary(page);
    const row = page.locator('.track');
    await clickTrackAction(row, 'Download');
    await expect(page.getByRole('alert')).toContainText('Synthetic download failure');
    await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Download', exact: true, includeHidden: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls.includes('readFile'))).toBe(false);
});

test('protected fixture audio explains the restriction and never requests a download', async ({ page }) => {
    await installTelegramFixture(page, 'protected');
    await page.goto('/');
    await connectFixture(page);

    await addFixtureSource(page, ':)');
    await openLibrary(page);
    const row = page.locator('.track');
    await expect(row.getByRole('button', { name: 'Download', exact: true, includeHidden: true })).toBeDisabled();
    await expect(row.getByRole('button', { name: 'Stream', exact: true })).toBeDisabled();
    await expect(row).toContainText('This source restricts saving');
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
});

test('channel discovery builds a distinct catalog without indexing audio; selection and filters persist', async ({ page }) => {
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    const catalog = page.getByRole('region', { name: 'Find your sources' });
    const picker = page.getByRole('region', { name: 'Choose a source' });
    await expect(catalog).toContainText('Source list is ready.');
    await expect(picker.locator('.channel-row')).toHaveCount(4);
    await expect(page.locator('.track')).toHaveCount(0);
    expect(await page.evaluate(() => fixtureCalls.includes('searchChatMessages'))).toBe(false);
    const search = picker.getByRole('searchbox', { name: 'Search discovered sources' });
    await search.fill('cafe');
    await expect(picker.locator('.channel-row')).toHaveCount(1);
    await expect(picker.locator('.channel-details > strong')).toHaveText('Café Sessions');
    await search.fill('missing channel');
    await expect(picker).toContainText('No matching sources');
    await picker.getByRole('button', { name: 'Show all sources', exact: true }).click();
    await addFixtureSource(page, 'Café Sessions');
    await expect(picker.getByRole('button', { name: 'Added to library: Café Sessions', exact: true })).toBeDisabled();
    await expectStoredTracks(page, 1);
    await picker.getByRole('button', { name: 'In library 1', exact: true }).click();
    await expect(picker.locator('.channel-row')).toHaveCount(1);
    await picker.getByRole('button', { name: 'Not added 3', exact: true }).click();
    await expect(picker.locator('.channel-row')).toHaveCount(3);
    await picker.getByRole('button', { name: 'All 4', exact: true }).click();
    for (const width of [1280, 820, 390]) {
        await page.setViewportSize({ width, height: 900 });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await picker.screenshot({ path: 'artifacts/channel-picker-' + width + '.png' });
    }
    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openChannels(page);
    await expect(picker.locator('.channel-row')).toHaveCount(4);
    await expect(picker.getByRole('button', { name: 'Added to library: Café Sessions', exact: true })).toBeDisabled();
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
});

test('channel discovery can pause, resume, and recover from an error without indexing audio', async ({ page }) => {
    await installTelegramFixture(page, 'sync');
    await page.addInitScript(() => { globalThis.fixtureHoldDiscovery = true; });
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    const catalog = page.getByRole('region', { name: 'Find your sources' });
    await expect.poll(() => page.evaluate(() => typeof releaseFixtureDiscovery)).toBe('function');
    await catalog.getByRole('button', { name: 'Pause discovery', exact: true }).click();
    await expect(catalog).toContainText('Discovery paused');
    await page.evaluate(() => { fixtureHoldDiscovery = false; releaseFixtureDiscovery(); });
    await catalog.getByRole('button', { name: 'Resume discovery', exact: true }).click();
    await expect(catalog).toContainText('Source list is ready.');
    await page.evaluate(() => { globalThis.fixtureDiscoveryFailure = true; });
    await catalog.getByRole('button', { name: 'Refresh source list', exact: true }).click();
    await expect(catalog.getByRole('alert')).toContainText('Synthetic discovery failure');
    await page.evaluate(() => { globalThis.fixtureDiscoveryFailure = false; });
    await expect(catalog).toContainText('Source list is ready.');
    await expect(page.locator('.channel-row')).toHaveCount(4);
    expect(await page.evaluate(() => fixtureCalls.includes('searchChatMessages'))).toBe(false);
});

test('audio job indexes only chosen channels and permits choosing another channel while running', async ({ page }) => {
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    const picker = page.getByRole('region', { name: 'Choose a source' });
    await expect(picker.locator('.channel-row')).toHaveCount(4);
    await page.evaluate(() => { globalThis.fixtureHoldIndex = true; });
    await addFixtureSource(page, ':)');
    const sync = page.getByRole('region', { name: 'Audio from selected sources' });
    await expect(sync).toContainText('Indexing audio history');
    await expect(picker.getByRole('button', { name: 'Add to library: Café Sessions', exact: true })).toBeEnabled();
    await addFixtureSource(page, 'Café Sessions');
    await page.evaluate(() => { fixtureHoldIndex = false; for (const release of releaseFixtureIndexes) release(); });
    await expect(sync).toContainText('2 selected sources · 2 fully indexed');
    await expect(sync).not.toContainText('skipped');
    await expectStoredTracks(page, 2);
    expect(await page.evaluate(() => ({ keys: new Set(fixturePages.map(x => x.chat + ':' + x.cursor)).size, chats: [...new Set(fixturePages.map(x => x.chat))].sort(), downloaded: fixtureCalls.includes('downloadFile') })))
        .toEqual({ keys: 4, chats: [-100123, -100124].sort(), downloaded: false });
    const ids = await page.evaluate(async p => JSON.parse(await (await import(p)).searchLibrary(700, '', 0, 100)).map(x => x.id).sort(), modulePath);
    await sync.getByRole('button', { name: 'Refresh now', exact: true }).click();
    await expect(sync).toContainText('2 selected sources · 2 fully indexed');
    expect(await page.evaluate(async p => JSON.parse(await (await import(p)).searchLibrary(700, '', 0, 100)).map(x => x.id).sort(), modulePath)).toEqual(ids);
    for (const width of [1280, 390]) {
        await page.setViewportSize({ width, height: 900 });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.locator('.content').screenshot({ path: 'artifacts/channel-sync-' + width + '.png' });
    }
});

test('selected audio resumes after reload and explicit pause prevents auto resume', async ({ page }) => {
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    await page.evaluate(() => { globalThis.fixtureHoldIndex = true; });
    await addFixtureSource(page, ':)');
    const sync = page.getByRole('region', { name: 'Audio from selected sources' });
    await expect(sync).toContainText('Indexing audio history');
    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openChannels(page);
    await expect(sync).toContainText('1 selected sources · 1 fully indexed');
    await expectStoredTracks(page, 1);
    await page.evaluate(async p => {
        const library = await import(p);
        const source = JSON.parse(await library.readSource(700, -100123));
        await library.savePage(JSON.stringify({ ...source, historyComplete: false, historyCursor: 10 }), '[]');
        globalThis.fixtureHoldIndex = true;
    }, modulePath);
    await sync.getByRole('button', { name: 'Refresh now', exact: true }).click();
    await expect(sync).toContainText('Indexing audio history');
    await sync.getByRole('button', { name: 'Pause sync', exact: true }).click();
    await expect(sync).toContainText('Sync paused. Your progress is saved.');
    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openChannels(page);
    await expect(sync.getByRole('button', { name: 'Resume sync', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls.includes('searchChatMessages'))).toBe(false);
    await sync.getByRole('button', { name: 'Resume sync', exact: true }).click();
    await expect(sync).toContainText('1 selected sources · 1 fully indexed');
    expect(await page.evaluate(() => fixturePages[0])).toEqual({ chat: -100123, cursor: 10 });
});

test('an inaccessible selected channel does not index unselected sources or stop the other selection', async ({ page }) => {
    await installTelegramFixture(page, 'sync-failure');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    await page.evaluate(() => { globalThis.fixtureHoldIndex = true; });
    await addFixtureSource(page, ':)');
    await addFixtureSource(page, 'آهنگ‌های من 🎵');
    await page.evaluate(() => { fixtureHoldIndex = false; for (const release of releaseFixtureIndexes) release(); });
    const sync = page.getByRole('region', { name: 'Audio from selected sources' });
    await expect(sync).toContainText('2 selected sources · 1 fully indexed');
    await expect(sync).toContainText('2 selected sources · 1 fully indexed');
    await expect(sync.getByRole('alert')).toContainText('Synthetic inaccessible channel');
    await expect(sync.getByRole('button', { name: 'Retry sync', exact: true })).toBeEnabled();
    await expectStoredTracks(page, 1);
    expect(await page.evaluate(() => fixturePages.some(x => ![-100123, -100125].includes(x.chat)))).toBe(false);
});

test('unindex removes audio and playlist entries, stops playback, and permits a fresh index', async ({ page }) => {
    await installTelegramFixture(page, 'sync');
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    const picker = page.getByRole('region', { name: 'Choose a source' });
    await expect(picker.locator('.channel-row')).toHaveCount(4);
    for (const name of [':)', 'Café Sessions']) {
        await addFixtureSource(page, name);
        await expect(picker.getByRole('button', { name: `Added to library: ${name}`, exact: true })).toBeVisible();
    }
    await openLibrary(page);
    const removedRow = page.locator('.track').filter({ has: page.locator('.music-source').filter({ hasText: ':)' }) });
    await removedRow.getByRole('button', { name: 'Favorite track', exact: true }).click();
    await clickTrackAction(removedRow, 'Add to playlist');
    await clickTrackAction(page.locator('.track').filter({ has: page.locator('.music-source').filter({ hasText: 'Café Sessions' }) }), 'Add to playlist');
    await removedRow.getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(removedRow.getByRole('status')).toHaveText('Now playing');
    for (const width of [1280, 800, 390]) {
        await page.setViewportSize({ width, height: 900 });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
    await openChannels(page);
    await picker.screenshot({ path: 'artifacts/channel-unindex-mobile.png' });
    await picker.getByRole('button', { name: 'Remove from library: :)', exact: true }).click();
    await expect(picker.getByRole('status')).toContainText('Unindexed :)');
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(1);
    await expect(page.locator('.music-tabs')).toContainText('Listening list 1');
    await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => fixtureAudio.paused)).toBe(true);
    await openChannels(page);
    await expect(picker.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeEnabled();
    await expect(picker.locator('.channel-row')).toHaveCount(4);
    await page.reload(); await openRoot(page, 'Profile'); await connectFixture(page);
    await openChannels(page);
    await expectStoredTracks(page, 1);
    await expect(picker.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
    await addFixtureSource(page, ':)');
    await expectStoredTracks(page, 2);
    await openLibrary(page);
    await expect(removedRow.locator('.track-favorite')).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByRole('button', { name: /Listening list 1/ })).toBeVisible();
});

test('unindex during background indexing rejects late pages and continues the other selection', async ({ page }) => {
    await installTelegramFixture(page, 'sync'); await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    const picker = page.getByRole('region', { name: 'Choose a source' });
    await page.evaluate(() => { globalThis.fixtureHoldIndex = true; });
    await addFixtureSource(page, ':)');
    await expect.poll(() => page.evaluate(() => globalThis.fixturePages?.length ?? 0)).toBe(1);
    await addFixtureSource(page, 'Café Sessions');
    await picker.getByRole('button', { name: 'Remove from library: :)', exact: true }).click();
    await expect(picker.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeEnabled();
    await page.evaluate(() => { fixtureHoldIndex = false; for (const release of releaseFixtureIndexes) release(); });
    const sync = page.getByRole('region', { name: 'Audio from selected sources' });
    await expect(sync).toContainText('1 selected sources · 1 fully indexed');
    await expect(sync).toContainText('1 selected sources · 1 fully indexed');
    await expectStoredTracks(page, 1);
    expect(await page.evaluate(async p => JSON.parse(await (await import(p)).searchLibrary(700, '', 0, 100)).map(t => t.origin.sourceName), modulePath)).toEqual(['Café Sessions']);
    expect(await page.evaluate(async p => await (await import(p)).readSource(700, -100123), modulePath)).toBeNull();
    await expect(page.getByRole('alert')).toHaveCount(0);
});

test('IndexedDB unindex is channel and account scoped and preserves unrelated state', async ({ page }) => {
    await page.goto('/');
    const actual = await page.evaluate(async p => {
        const lib = await import(p), ids = [];
        for (const [account, chat] of [[700, 1], [700, 10], [701, 1]]) {
            const id = crypto.randomUUID(); ids.push(id);
            await lib.savePage(JSON.stringify({ accountId: account, chatId: chat, name: 'fixture' }), JSON.stringify([{ id, identity: `${account}:${chat}:1`, origin: { accountId: account, chatId: chat }, searchText: 'fixture', favorite: true }]));
        }
        await lib.saveState(700, 'playlist', JSON.stringify({ Id: crypto.randomUUID(), Name: 'Saved', Tracks: [ids[0], ids[1], ids[0]] }));
        await lib.saveState(700, 'channel-catalog-v1', 'catalog stays');
        const removed = JSON.parse(await lib.removeSource(700, 1));
        return { removed, repeated: JSON.parse(await lib.removeSource(700, 1)), ids,
            kept: JSON.parse(await lib.searchLibrary(700, '', 0, 100)).map(x => x.id),
            other: JSON.parse(await lib.searchLibrary(701, '', 0, 100)).map(x => x.id),
            playlist: JSON.parse(await lib.readState(700, 'playlist')).Tracks,
            catalog: await lib.readState(700, 'channel-catalog-v1') };
    }, modulePath);
    expect(actual.removed).toEqual([actual.ids[0]]); expect(actual.repeated).toEqual([]);
    expect(actual.kept).toEqual([actual.ids[1]]); expect(actual.other).toEqual([actual.ids[2]]);
    expect(actual.playlist).toEqual([actual.ids[1]]); expect(actual.catalog).toBe('catalog stays');
});

test('Download exports the original file without playing and supports cancellation', async ({ page }) => {
    await installTelegramFixture(page); await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page, ':)');
    await openLibrary(page);
    const row = page.locator('.track');
    await clickTrackAction(row, 'Download');
    await expect(page.getByRole('region', { name: 'Active transfer', exact: true }).getByRole('status')).toContainText('Downloading');
    await expect(row.getByRole('button', { name: 'Stream', exact: true })).toBeDisabled();
    const savedFile = page.waitForEvent('download');
    await page.evaluate(() => completeFixtureDownload());
    await savePreparedExport(page);
    const download = await savedFile;
    expect(download.suggestedFilename()).toBe('fixture.wav');
    const bytes = await fs.readFile(await download.path());
    expect(bytes.length).toBe(80044); expect(bytes.toString('ascii', 0, 4)).toBe('RIFF');
    await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => !!globalThis.fixtureAudio)).toBe(false);
    await expect(page.locator('.track')).toHaveCount(1);
    await clickTrackAction(row, 'Download');
    await page.getByRole('region', { name: 'Active transfer', exact: true }).getByRole('button', { name: 'Cancel download', exact: true }).click();
    await expect(page.locator('.music-feedback')).toContainText('Download canceled');
    expect(await page.evaluate(() => fixtureCalls.includes('cancelDownloadFile'))).toBe(true);
});

test('signing out cancels a separate download before erasing account data', async ({ page }) => {
    await installTelegramFixture(page); await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page, ':)');
    await openLibrary(page);
    await clickTrackAction(page.locator('.track'), 'Download');
    await expect(page.getByRole('region', { name: 'Active transfer', exact: true }).getByRole('status')).toContainText('Downloading');
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.locator('.music-content')).toBeHidden();
    await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => fixtureCalls.includes('cancelDownloadFile'))).toBe(true);
    expect(await page.evaluate(async p => JSON.parse(await (await import(p)).readSources(700)).length, modulePath)).toBe(0);
    await expect(page.getByRole('alert')).toHaveCount(0);
});

test('Stream waits through a cold connection longer than 15 seconds and then plays', async ({ page }) => {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page, ':)');
    await openLibrary(page);
    await page.evaluate(() => { globalThis.fixtureHoldFirstStream = true; });
    await page.locator('.track').getByRole('button', { name: 'Stream', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof releaseFixtureFirstStream)).toBe('function');
    await page.waitForTimeout(16000);
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('alert')).toHaveCount(0);
    await expect(page.locator('.track').getByRole('status')).toHaveText('Buffering audio…');
    await page.evaluate(() => { fixtureHoldFirstStream = false; releaseFixtureFirstStream(); });
    await expect(page.locator('.track').getByRole('status')).toHaveText('Now playing');
    await stopPlayback(page);
});

test('Stream transfer errors show immediately, unlock Download, and allow retry', async ({ page }) => {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page, ':)');
    await openLibrary(page);
    await page.evaluate(() => { globalThis.fixtureStreamFailure = true; });
    const row = page.locator('.track');
    await row.getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('alert')).toContainText('Synthetic stream transfer failure');
    await expect(row.getByRole('status')).toHaveText('Playback stopped');
    await expect(row.getByRole('button', { name: 'Download', exact: true, includeHidden: true })).toBeEnabled();
    await player.getByRole('button', { name: 'Retry stream', exact: true }).click();
    await expect(row.getByRole('status')).toHaveText('Now playing');
    await stopPlayback(page);
});

test('Cancel stream interrupts startup while Telegram is still sending the first chunk', async ({ page }) => {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page, ':)');
    await openLibrary(page);
    await page.evaluate(() => { globalThis.fixtureHoldFirstStream = true; });
    const row = page.locator('.track');
    await row.getByRole('button', { name: 'Stream', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof releaseFixtureFirstStream)).toBe('function');
    await page.getByRole('button', { name: 'Cancel stream', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Download', exact: true, includeHidden: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls.includes('cancelDownloadFile'))).toBe(true);
    await page.evaluate(() => { fixtureHoldFirstStream = false; releaseFixtureFirstStream(); });
    await row.getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(row.getByRole('status')).toHaveText('Now playing');
});

test('Stream starts before the remaining audio arrives and Stop invalidates its URL', async ({ page }) => {
    await installTelegramFixture(page, 'progressive'); await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page, ':)');
    await openLibrary(page);
    await page.evaluate(() => { globalThis.fixtureHoldStream = true; });
    await page.locator('.track').getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('status')).toContainText('Streaming from Telegram');
    await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(0);
    expect(await page.evaluate(() => fixtureDownloads.every(x => x.limit > 0))).toBe(true);
    const url = await page.evaluate(() => fixtureAudio.src);
    await stopPlayback(page);
    await page.evaluate(() => { fixtureHoldStream = false; globalThis.releaseFixtureStream?.(); });
    await expect(player).toHaveCount(0);
    expect(await page.evaluate(async url => (await fetch(url)).status, url)).toBe(410);
    expect(await page.evaluate(() => fixtureAudio.paused)).toBe(true);
});

test('music source filters, favorites, listening list and next-track controls work on desktop and mobile', async ({ page }) => {
    await installTelegramFixture(page, 'sync'); await page.goto('/');
    await connectFixture(page);
    for (const name of [':)', 'Café Sessions', 'آهنگ‌های من 🎵']) {
        await addFixtureSource(page, name);
        await expect(page.getByRole('button', { name: `Added to library: ${name}`, exact: true })).toBeVisible();
    }
    await openLibrary(page);
    await page.evaluate(async p => {
        const lib = await import(p), sources = JSON.parse(await lib.readSources(700)), tracks = JSON.parse(await lib.searchLibrary(700, '', 0, 100));
        const names = ['Rain on the Balcony', 'A live recording with a very long title that still needs room to breathe', 'آخر شب'];
        for (let i = 0; i < tracks.length; i++) await lib.savePage(JSON.stringify(sources.find(s => s.chatId === tracks[i].origin.chatId)), JSON.stringify([{ ...tracks[i], title: names[i], performer: i === 2 ? 'هنرمند آزمایشی' : 'Original test artist' }]));
    }, modulePath);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    const library = page.getByRole('region', { name: 'Local library', exact: true });
    const first = library.locator('.track').first();
    await first.getByRole('button', { name: 'Favorite track', exact: true }).click();
    await library.getByRole('button', { name: 'Liked Songs', exact: true }).click();
    await expect(library.locator('.track')).toHaveCount(1);
    await clickTrackAction(first, 'Add to playlist');
    await library.getByRole('button', { name: /Listening list 1/ }).click();
    await expect(library.locator('.track')).toHaveCount(1);
    await library.getByRole('button', { name: 'All tracks', exact: true }).click();
    await selectChannel(library, '-100123');
    await expect(library.locator('.track')).toHaveCount(1);
    await expect(library.locator('.music-source')).toContainText(':)');
    await selectChannel(library, '');
    await first.getByRole('button', { name: 'Stream', exact: true }).click();
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player.getByRole('status')).toContainText('Streaming from Telegram');
    await player.getByRole('button', { name: 'Next track', exact: true }).click();
    await expect(player).toContainText('A live recording');
    await expect(player).toContainText('Source · Café Sessions');
    await player.getByRole('button', { name: 'Pause playback', exact: true }).click();
    for (const width of [1280, 820, 390]) {
        await page.setViewportSize({ width, height: 980 });
        await library.evaluate(el => window.scrollTo(0, el.getBoundingClientRect().top + scrollY - 24));
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.screenshot({ path: `artifacts/music-library-${width}.png` });
    }
    await stopPlayback(page);
});

test('music home renders and fits a mobile viewport', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: /Your sources.*Your kind of music/ })).toBeVisible({ timeout: 15000 });
    await expect(page.getByRole('button', { name: 'Connect account' })).toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('IndexedDB preserves 10,000 synthetic records, local edits, and account isolation across reload', async ({ page }) => {
    await page.goto('/');
    const saved = await page.evaluate(async modulePath => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -1001234567890, name: 'Explicit test fixture', historyCursor: 9007199254740000 };
        const tracks = Array.from({ length: 10000 }, (_, i) => ({ id: crypto.randomUUID(), identity: `700:${source.chatId}:${i}`, origin: { accountId: 700, chatId: source.chatId, messageId: i }, title: `Synthetic ${i}`, searchText: `synthetic ${i}`, favorite: false }));
        for (let i = 0; i < tracks.length; i += 100) await lib.savePage(JSON.stringify(source), JSON.stringify(tracks.slice(i, i + 100)));
        await lib.favorite(700, tracks[42].id, true);
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...tracks[42], id: crypto.randomUUID(), title: 'Edited', searchText: 'edited' }]));
        await lib.saveState(700, 'playlist', JSON.stringify({ tracks: [tracks[42].id] }));
        await lib.savePage(JSON.stringify({ ...source, accountId: 701 }), JSON.stringify([{ ...tracks[0], id: crypto.randomUUID(), identity: '701:other', origin: { accountId: 701, chatId: source.chatId, messageId: 0 } }]));
        return tracks[42].id;
    }, modulePath);
    await page.reload();
    const actual = await page.evaluate(async modulePath => {
        const lib = await import(modulePath); let count = 0;
        for (let offset = 0; offset < 11000; offset += 1000) count += JSON.parse(await lib.searchLibrary(700, '', offset, 1000)).length;
        return { count, edited: JSON.parse(await lib.searchLibrary(700, 'edited', 0, 10)), other: JSON.parse(await lib.searchLibrary(701, '', 0, 10)), sources: JSON.parse(await lib.readSources(700)), playlist: await lib.readState(700, 'playlist') };
    }, modulePath);
    expect(actual.count).toBe(10000); expect(actual.edited).toHaveLength(1);
    expect(actual.edited[0].id).toBe(saved); expect(actual.edited[0].favorite).toBe(true);
    expect(actual.other).toHaveLength(1); expect(actual.sources[0].historyCursor).toBe(9007199254740000);
    expect(actual.playlist).toContain(saved);
    const cleared = await page.evaluate(async modulePath => {
        const lib = await import(modulePath); await lib.clearAccount(700);
        return [await lib.searchLibrary(700, '', 0, 10), await lib.readSources(700), await lib.readState(700, 'playlist'), JSON.parse(await lib.searchLibrary(701, '', 0, 10)).length];
    }, modulePath);
    expect(cleared).toEqual(['[]', '[]', null, 1]);
});

test('a second tab cannot take over the active profile', async ({ page, context }) => {
    await page.goto('/'); await page.evaluate(async p => (await import(p)).openLibrary(), modulePath);
    const other = await context.newPage(); await other.goto('/');
    const error = await other.evaluate(async p => { try { await (await import(p)).openLibrary(); return ''; } catch (e) { return e.message; } }, modulePath);
    expect(error).toContain('already open in another tab');
    await page.close();
    await other.reload();
    // Closing a renderer releases its Web Locks asynchronously, especially while
    // an offline worker is active. Verify eventual acquisition instead of racing close().
    await expect.poll(() => other.evaluate(async p => { try { await (await import(p)).openLibrary(); return true; } catch { return false; } }, modulePath)).toBe(true);
});

test('storage denial surfaces an actionable error', async ({ page }) => {
    await page.addInitScript(() => { indexedDB.open = () => { throw new DOMException('Storage denied by test', 'SecurityError'); }; });
    await page.goto('/');
    const error = await page.evaluate(async p => { try { await (await import(p)).openLibrary(); return ''; } catch (e) { return e.message; } }, modulePath);
    expect(error).toMatch(/storage|Storage/);
});

test('a mismatched Telegram bundle is refused before a worker starts', async ({ page }) => {
    await page.route('**/tdweb/channelwave-build.json', route => route.fulfill({ json: { revision: 'wrong-revision', wrapperPatch: 1 } }));
    let workerStarted = false; page.on('worker', () => { workerStarted = true; });
    await page.goto('/'); await openRoot(page, 'Profile'); await page.getByText('Connection details', { exact: true }).click(); await page.getByRole('button', { name: 'Check Telegram engine' }).click();
    await expect(page.getByRole('alert')).toContainText('do not match the pinned schema');
    expect(workerStarted).toBe(false);
});

test('missing developer configuration is actionable and contains no JavaScript stack', async ({ page }) => {
    await page.route('**/channelwave.local.json', route => route.fulfill({ status: 404, body: '' }));
    await page.goto('/'); await page.getByRole('button', { name: 'Connect account' }).click();
    await expect(page.getByRole('alert')).toContainText('Developer configuration is missing');
    await expect(page.getByRole('alert')).not.toContainText('at http');
});

test('session deletion removes the pinned TDLib stores and preserves unrelated data', async ({ page }) => {
    await page.goto('/');
    const remaining = await page.evaluate(async modulePath => {
        for (const name of ['channelwave-telegram', '/channelwave-telegram/dbfs', 'unrelated-test-store']) {
            await new Promise((resolve, reject) => { const open = indexedDB.open(name, 1); open.onupgradeneeded = () => open.result.createObjectStore('fixture'); open.onsuccess = () => { open.result.close(); resolve(); }; open.onerror = () => reject(open.error); });
        }
        const lib = await import(modulePath); await lib.clearTelegramStorage();
        return (await indexedDB.databases()).map(db => db.name);
    }, modulePath);
    expect(remaining).toContain('unrelated-test-store');
    expect(remaining).not.toContain('channelwave-telegram');
    expect(remaining).not.toContain('/channelwave-telegram/dbfs');
});

test('audio decodes an original test tone, rejects native paths and explains autoplay denial', async ({ page }) => {
    await page.goto('/');
    const result = await page.evaluate(async modulePath => {
        const media = await import(modulePath);
        // Original PCM silence; no third-party recording or network URL.
        const buffer = new ArrayBuffer(44 + 8000 * 2), data = new DataView(buffer);
        const text = (offset, value) => [...value].forEach((v, i) => data.setUint8(offset + i, v.charCodeAt(0)));
        text(0, 'RIFF'); data.setUint32(4, buffer.byteLength - 8, true); text(8, 'WAVE'); text(12, 'fmt '); data.setUint32(16, 16, true); data.setUint16(20, 1, true); data.setUint16(22, 1, true); data.setUint32(24, 8000, true); data.setUint32(28, 16000, true); data.setUint16(32, 2, true); data.setUint16(34, 16, true); text(36, 'data'); data.setUint32(40, 16000, true);
        const url = URL.createObjectURL(new Blob([buffer], { type: 'audio/wav' }));
        await media.loadAudio(url); await media.playAudio(); await media.pauseAudio(); await media.seekAudio(0.5);
        const realPlay = HTMLMediaElement.prototype.play;
        HTMLMediaElement.prototype.play = () => Promise.reject(new DOMException('Test denial', 'NotAllowedError'));
        let denied = ''; try { await media.playAudio(); } catch (e) { denied = e.message; } finally { HTMLMediaElement.prototype.play = realPlay; }
        let native = ''; try { await media.loadAudio('C:/private/music.mp3'); } catch (e) { native = e.message; }
        media.clearAudio();
        return { denied, native };
    }, modulePath);
    expect(result.denied).toContain('Press Resume'); expect(result.native).toContain('Blob URLs');
});
