import { openRoot } from './fixtures/telegram.mjs';
import { expectLoadedTracks } from './fixtures/library.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, openLibrary, openChannels, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';
import { installLoadingGates } from './fixtures/loading.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const content = page => page.locator('.music-results');
const pending = (page, name, count = 1) => expect.poll(() => page.evaluate(n => fixtureOperationPending(n), name)).toBe(count);
const hold = (page, name, query = null, stage = 'before') => page.evaluate(([n, q, s]) => fixtureHoldOperation(n, q, s), [name, query, stage]);
const release = (page, name, fail = false) => page.evaluate(([n, f]) => fixtureReleaseOperation(n, f), [name, fail]);
async function capture(page, name) {
    await fs.mkdir('artifacts/loading', { recursive: true });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/loading/${name}-${page.viewportSize().width}.png` });
}
async function seed(page, count = 1, initialHolds = []) {
    await installTelegramFixture(page);
    await installLoadingGates(page, initialHolds);
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    await page.evaluate(async ({ path, count }) => {
        const lib = await import(path);
        const source = { accountId: 700, chatId: -100123, name: 'Fixture source', historyComplete: true };
        const tracks = Array.from({ length: count }, (_, i) => ({
            id: crypto.randomUUID(), identity: `700:-100123:${String(i + 1).padStart(5, '0')}`,
            origin: { accountId: 700, chatId: -100123, messageId: i + 1, sourceName: source.name, protected: false },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
            originalTitle: `Song ${i + 1}`, originalPerformer: 'Fixture artist', title: `Song ${i + 1}`, performer: 'Fixture artist',
            duration: 5, publishedAt: '2026-01-01T00:00:00+00:00', available: true, favorite: false,
            searchText: `song ${i + 1} fixture artist fixture source`
        }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
    }, { path: modulePath, count });
    await connectFixture(page);
    await expect(page.locator('.sidebar-bottom')).toHaveText('Connected to Telegram');
    await openLibrary(page);
    await expectLoadedTracks(page, Math.min(count, 100));
    await expect(content(page)).toHaveAttribute('aria-busy', 'false');
}

test('background source reconciliation updates music without foreground loading progress', async ({ page }) => {
    await installTelegramFixture(page, 'progressive'); await installLoadingGates(page);
    await page.goto('/'); await connectFixture(page); await addFixtureSource(page); await openLibrary(page);
    await expectLoadedTracks(page, 1);
    await expect(content(page)).toHaveAttribute('aria-busy', 'false');
    await hold(page, 'searchLibrary');
    await page.evaluate(async () => {
        const original = await fixtureClient.send({ '@type': 'getMessage', chat_id: -100123, message_id: 1 });
        original.chat_id = -100123;
        const message = structuredClone(original);
        message.id = 2; message.content.audio.title = 'Quietly added';
        globalThis.fixtureMessages = { 1: original, 2: message };
        fixtureClient.options.onUpdate({ '@type': 'updateNewMessage', message });
    });
    await pending(page, 'searchLibrary');
    await expect(content(page)).toHaveAttribute('aria-busy', 'false');
    await expect(page.getByText('Loading your music…', { exact: true })).toHaveCount(0);
    await expect(page.locator('.music-count')).not.toContainText('Updating your music');
    await expectLoadedTracks(page, 1);
    await release(page, 'searchLibrary');
    await expectLoadedTracks(page, 2);
    await expect(page.locator('.music-track-title strong').first()).toHaveText('Quietly added');
});

for (const width of [1440, 390]) {
    test(`branded boot and local initialization handoff at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await installTelegramFixture(page);
        await installLoadingGates(page, [{ name: 'readState', query: 'pending-signout-v1', stage: 'before' }]);
        let resume;
        const boot = new Promise(resolve => { resume = resolve; });
        await page.route('**/_framework/blazor.webassembly.js', async route => { await boot; await route.continue(); });
        await page.goto('/', { waitUntil: 'commit' });
        await expect(page.locator('.startup-screen')).toBeVisible();
        await expect(page.getByRole('status')).toHaveText('Opening your music…');
        await capture(page, 'startup');
        resume();
        await pending(page, 'readState');
        await expect(page.locator('.startup-screen')).toBeVisible();
        await release(page, 'readState');
        await expect(page.locator('.startup-screen')).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    });

    test(`skeletons, search refresh and empty results at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await seed(page);
        await hold(page, 'searchLibrary');
        await page.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true }).click();
        await pending(page, 'searchLibrary');
        await expect(content(page)).toHaveAttribute('aria-busy', 'true');
        await expect(page.locator('.loading-skeleton-tracks .skeleton-row')).toHaveCount(6);
        await expect(page.locator('.music-empty')).toHaveCount(0);
        await expect(page.locator('.music-count')).not.toContainText('0 tracks');
        expect(await page.locator('.library-loading-status').evaluate(el => el.closest('[aria-busy="true"]') === null)).toBe(true);
        await expect(page.locator('.loading-skeleton')).toHaveCSS('opacity', '1');
        await capture(page, 'library-skeleton');
        await page.emulateMedia({ reducedMotion: 'reduce' });
        const animations = await page.locator('.loading-skeleton').evaluate(el => el.getAnimations({ subtree: true }).filter(a => a.playState === 'running').length);
        expect(animations).toBe(0);
        await release(page, 'searchLibrary');
        await expect(page.getByRole('heading', { name: 'Keep the songs you love' })).toBeVisible();
        await page.locator('.music-tabs').getByRole('button', { name: 'All tracks', exact: true }).click();
        await expect(page.locator('.track')).toHaveCount(1);
        await hold(page, 'searchLibrary', 'missing');
        await page.getByRole('searchbox', { name: 'Search your indexed library' }).fill('missing');
        await page.getByRole('button', { name: 'Search', exact: true }).click();
        await pending(page, 'searchLibrary');
        await expect(page.locator('.track')).toHaveCount(1);
        await expect(page.locator('.music-empty')).toHaveCount(0);
        await release(page, 'searchLibrary');
        await expect(page.getByRole('heading', { name: 'No matches just yet' })).toBeVisible();
        await expect(content(page)).toHaveAttribute('aria-busy', 'false');
        await hold(page, 'searchLibrary', 'another');
        await page.getByRole('searchbox', { name: 'Search your indexed library' }).fill('another');
        await page.getByRole('button', { name: 'Search', exact: true }).click();
        await pending(page, 'searchLibrary');
        await release(page, 'searchLibrary', true);
        await expect(page.getByRole('button', { name: 'Retry loading music' })).toBeVisible();
        await expect(page.locator('.music-empty')).toHaveCount(0);
        await page.getByRole('button', { name: 'Retry loading music' }).click();
        await expect(page.getByRole('heading', { name: 'No matches just yet' })).toBeVisible();
    });
}

test('older requests cannot clear the current loading state', async ({ page }) => {
    await seed(page);
    await hold(page, 'searchLibrary', '', 'after');
    await page.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true }).click();
    await pending(page, 'searchLibrary');
    await hold(page, 'searchLibrary', '', 'after');
    await page.locator('.music-tabs').getByRole('button', { name: 'All tracks', exact: true }).click();
    await pending(page, 'searchLibrary', 2);
    await release(page, 'searchLibrary');
    await expect(content(page)).toHaveAttribute('aria-busy', 'true');
    await expect(page.locator('.library-loading-status')).toBeVisible();
    await release(page, 'searchLibrary');
    await expect(content(page)).toHaveAttribute('aria-busy', 'false');
    await expect(page.locator('.track')).toHaveCount(1);
});

test('pagination keeps rows and playback, appends placeholders, and can recover from errors', async ({ page }) => {
    await seed(page, 101);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(page.locator('.music-player')).toBeVisible();
    await page.locator('.music-player').getByRole('button', { name: 'Pause playback', exact: true }).click();
    await hold(page, 'searchLibrary');
    await page.locator('#main-content').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await pending(page, 'searchLibrary');
    await expectLoadedTracks(page, 100);
    await expect(page.locator('.loading-skeleton-tracks .skeleton-row')).toHaveCount(3);
    await expect(page.locator('.music-player-status')).toHaveText('Paused');
    await release(page, 'searchLibrary', true);
    await expect(page.getByRole('alert')).toContainText('Synthetic loading failure');
    await expect(content(page)).toHaveAttribute('aria-busy', 'false');
    await page.getByRole('button', { name: 'Load more tracks', exact: true }).click();
    await expectLoadedTracks(page, 101);
    await expect(page.locator('.music-player-status')).toHaveText('Paused');
});

test('source discovery shows skeletons initially and retains discovered sources during refresh', async ({ page }) => {
    await installTelegramFixture(page);
    await page.addInitScript(() => { globalThis.fixtureHoldDiscovery = true; });
    await page.goto('/');
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await expect(page.locator('.loading-skeleton-sources .skeleton-row')).toHaveCount(3);
    await expect(page.locator('.channel-empty')).toHaveCount(0);
    await expect(page.locator('.loading-skeleton')).toHaveCSS('opacity', '1');
    await capture(page, 'source-skeleton');
    await page.evaluate(() => { fixtureHoldDiscovery = false; releaseFixtureDiscovery(); });
    await expect(page.locator('.channel-row')).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Refresh source list', exact: true })).toBeVisible();
    await page.evaluate(() => { fixtureHoldDiscovery = true; });
    await page.getByRole('button', { name: 'Refresh source list', exact: true }).click();
    await expect(page.locator('.channel-row')).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Pause discovery', exact: true })).toBeVisible();
    await page.evaluate(() => { fixtureHoldDiscovery = false; releaseFixtureDiscovery(); });
    await expect(page.locator('.channel-picker')).toHaveAttribute('aria-busy', 'false');
});

test('preparing audio keeps cancellation available and favorite saves do not show library skeletons', async ({ page }) => {
    await seed(page);
    await hold(page, 'loadAudio');
    await page.locator('.track').getByRole('button', { name: 'Stream', exact: true }).click();
    await pending(page, 'loadAudio');
    await expect(page.locator('.music-row-status .loading-spinner')).toBeVisible();
    await expect(page.locator('.music-player-status .loading-spinner')).toBeVisible();
    await page.locator('.music-player').getByRole('button', { name: 'Open full-screen player' }).click();
    const full = page.getByRole('dialog', { name: 'Music player', exact: true });
    await expect(full.locator('.full-player-status .loading-spinner')).toBeVisible();
    await expect(full.getByRole('button', { name: 'Cancel stream', exact: true })).toBeEnabled();
    await full.getByRole('button', { name: 'Cancel stream', exact: true }).click();
    await release(page, 'loadAudio');
    await expect(full).toHaveCount(0);
    await expect(page.locator('.music-player')).toHaveCount(0);
    await hold(page, 'favorite');
    await clickTrackAction(page.locator('.track'), 'Favorite track');
    await pending(page, 'favorite');
    await expect(page.locator('.loading-skeleton-tracks')).toHaveCount(0);
    await expect(content(page)).toHaveAttribute('aria-busy', 'false');
    await expect(page.getByRole('status').filter({ hasText: 'Saving favorite' })).toBeVisible();
    await release(page, 'favorite');
    await expect(page.locator('.track').getByRole('button', { name: 'Remove favorite', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await page.locator('.track').getByRole('button', { name: 'Open player for Song 1', exact: true }).click();
    await hold(page, 'favorite');
    await full.getByRole('button', { name: 'Remove favorite', exact: true }).click();
    await pending(page, 'favorite');
    await expect(full.locator('.full-player-status')).toHaveText('Saving favorite…');
    await expect(full.locator('.full-player-titlebar .loading-spinner')).toBeVisible();
    await release(page, 'favorite');
    await expect(full.locator('.full-player-status')).toHaveText('Ready to play');
});

test('initialization failure exits the splash and exposes cleanup recovery', async ({ page }) => {
    await installTelegramFixture(page);
    await installLoadingGates(page, [{ name: 'readState', query: 'pending-signout-v1', stage: 'before' }]);
    await page.goto('/');
    await pending(page, 'readState');
    await release(page, 'readState', true);
    await expect(page.locator('.startup-screen')).toHaveCount(0);
    await expect(page.getByRole('alert')).toContainText('Synthetic loading failure');
    await page.evaluate(() => window.channelwaveOfflineReady);
    await expect(async () => {
        const retry = page.getByRole('button', { name: 'Retry local cleanup check' });
        if (await retry.isVisible()) await retry.click();
        await expect(retry).toHaveCount(0, { timeout: 1500 });
    }).toPass({ timeout: 15000 });
});

for (const success of [true, false]) {
    test(`artwork resolves to ${success ? 'a cover' : 'the fallback'} without a stuck placeholder`, async ({ page }) => {
        if (success) await page.addInitScript(() => {
            globalThis.fixtureArtwork = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII='), c => c.charCodeAt(0));
        });
        const operation = success ? 'readArtworkFile' : 'telegram:getMessage';
        await seed(page, 1, [{ name: operation, query: null, stage: 'before' }]);
        await pending(page, operation);
        const artwork = page.locator('.track .music-art');
        await expect(artwork).toHaveClass(/music-art-loading/);
        await expect(artwork).toHaveAttribute('aria-busy', 'true');
        await release(page, operation);
        await expect(artwork).toHaveAttribute('aria-busy', 'false');
        await expect(artwork).not.toHaveClass(/music-art-loading/);
        if (success) await expect(artwork.getByRole('img')).toBeVisible();
        else await expect(artwork.locator('.artwork-fallback')).toBeVisible();
    });
}

test('sign-out invalidates a pending read and exposes local cleanup progress', async ({ page }) => {
    await seed(page);
    await hold(page, 'searchLibrary', '', 'after');
    await page.locator('.music-tabs').getByRole('button', { name: 'Liked Songs', exact: true }).click();
    await pending(page, 'searchLibrary');
    await openRoot(page, 'Profile');
    await hold(page, 'clearAccount');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await pending(page, 'clearAccount');
    await expect(page.getByRole('status').filter({ hasText: 'Signing out and erasing local data' })).toBeVisible();
    await expect(page.locator('.loading-skeleton-tracks')).toHaveCount(0);
    await release(page, 'searchLibrary');
    await release(page, 'clearAccount');
    await expect(page.locator('.track')).toHaveCount(0);
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely' })).toBeVisible();
    await expect(page.locator('.connection-form')).toHaveAttribute('aria-busy', 'false');
});

test('indexing completed during a foreground read is refreshed after that read', async ({ page }) => {
    await installTelegramFixture(page);
    await installLoadingGates(page);
    await page.addInitScript(() => { globalThis.fixtureHoldIndex = true; });
    await page.goto('/');
    await connectFixture(page);
    await openChannels(page);
    await page.getByRole('button', { name: 'Add to library: :)', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof releaseFixtureIndex)).toBe('function');
    await openRoot(page, 'Library');
    await hold(page, 'searchLibrary', '', 'after');
    await page.getByRole('button', { name: /^Tracks \d/ }).click();
    await pending(page, 'searchLibrary');
    await page.evaluate(() => { fixtureHoldIndex = false; releaseFixtureIndex(); });
    await expect.poll(() => page.evaluate(async path => JSON.parse(await (await import(path)).readSource(700, -100123)).historyComplete, modulePath)).toBe(true);
    // The existing worker notification is coalesced for 250ms. Wait for its
    // persistence read, rather than advancing by an arbitrary test timeout.
    await hold(page, 'offlineTracks');
    await release(page, 'searchLibrary');
    await pending(page, 'offlineTracks');
    await release(page, 'offlineTracks');
    await expect(page.locator('.track')).toHaveCount(1);
    await expect(content(page)).toHaveAttribute('aria-busy', 'false');
});
