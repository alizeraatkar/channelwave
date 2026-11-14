import { openRoot } from './fixtures/telegram.mjs';
import { expectLoadedTracks } from './fixtures/library.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, openLibrary, clickTrackAction, selectChannel } from './fixtures/telegram.mjs';
import { installLoadingGates } from './fixtures/loading.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const cases = JSON.parse(await fs.readFile(new URL('./fixtures/search-cases.json', import.meta.url), 'utf8'));
const library = page => page.getByRole('region', { name: 'Local library', exact: true });
const rows = page => library(page).locator('.track');
const search = page => page.locator('#music-search');
const titles = page => rows(page).locator('.music-track-title > strong');
const pending = page => expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(1);
test.use({ timezoneId: 'Asia/Tehran' });

async function seed(page, count = null) {
    await installTelegramFixture(page);
    await installLoadingGates(page);
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled({ timeout: 60000 });
    await page.evaluate(async ({ modulePath, samples, count }) => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -100123, name: 'Fixture source', historyComplete: true };
        const tracks = Array.from({ length: count ?? samples.length }, (_, i) => {
            const sample = count ? { number: i + 1, title: `Synthetic ${i}`, artist: 'Scale artist', source: source.name, duration: 222, publishedAt: '2026-01-01T00:00:00Z' } : samples[i];
            return {
                id: crypto.randomUUID(), identity: `700:-100123:${String(sample.number).padStart(6, '0')}`,
                origin: { accountId: 700, chatId: -100123, messageId: sample.number, sourceName: sample.source, protected: false },
                media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
                originalTitle: sample.title, originalPerformer: sample.artist, title: sample.title, performer: sample.artist,
                duration: sample.duration, publishedAt: sample.publishedAt, available: true, favorite: sample.favorite ?? false,
                searchText: `${sample.title} ${sample.artist} ${sample.source}`.toLowerCase()
            };
        });
        for (let i = 0; i < tracks.length; i += 1000) await lib.savePage(JSON.stringify(source), JSON.stringify(tracks.slice(i, i + 1000)));
        globalThis.searchFixtureTracks = tracks;
        globalThis.searchFixtureSource = source;
        await lib.saveState(700, 'playlist', JSON.stringify({ Id: crypto.randomUUID(), Name: 'Listening list', Tracks: tracks.map(t => t.id).reverse() }));
        const other = { ...source, accountId: 701 };
        await lib.savePage(JSON.stringify(other), JSON.stringify([{ ...tracks[0], id: crypto.randomUUID(), identity: '701:-100123:1', origin: { ...tracks[0].origin, accountId: 701 } }]));
    }, { modulePath, samples: cases.tracks, count });
    await connectFixture(page);
    await expect(library(page).getByRole('heading', { name: 'Your music', exact: true })).toBeVisible({ timeout: count ? 60000 : 15000 });
    await openLibrary(page);
    await expectLoadedTracks(page, Math.min(count ?? cases.tracks.length, 100));
    await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'false');
}

test.afterEach(async ({ page }) => {
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('browser adapter agrees with shared matching cases and patches cached labels and favorites', async ({ page }) => {
    await seed(page);
    const actual = await page.evaluate(async ({ modulePath, tests }) => {
        const lib = await import(modulePath);
        // Tests send the same normalized term contract that BrowserLibrary emits.
        const normalize = value => value.normalize('NFD').toLowerCase().replace(/\p{Mn}/gu, '').replace(/[يى]/g, 'ی').replace(/ك/g, 'ک').replace(/[\u200c\u200d\u0640]/g, ' ').replace(/\s+/gu, ' ').trim();
        const terms = value => (value.match(/"[^"]*(?:"|$)|[^\s"]+/gu) ?? []).map(x => normalize(x.replace(/^"|"$/g, ''))).filter(Boolean);
        const request = (criteria = {}, extra = {}) => JSON.stringify({
            criteria: { text: '', title: '', artist: '', minDuration: null, maxDuration: null, publishedFrom: null, publishedBefore: null, ...criteria },
            terms: terms(criteria.text ?? ''), titleTerms: terms(criteria.title ?? ''), artistTerms: terms(criteria.artist ?? ''),
            favorites: false, chat: null, playlist: null, sort: 0, offset: 0, limit: 100, ...extra
        });
        const results = [];
        for (const test of tests) results.push(JSON.parse(await lib.searchLibraryAdvanced(700, request(test.criteria))).map(t => t.origin.messageId));
        const last = searchFixtureTracks.at(-1);
        await lib.setLocalMetadata(700, last.id, 'Sunrise Café', 'Local artist');
        await lib.favorite(700, last.id, true);
        const patched = JSON.parse(await lib.searchLibraryAdvanced(700, request({ text: 'cafe sunrise' }, { favorites: true, limit: 1 })));
        const playlist = JSON.parse(await lib.searchLibraryAdvanced(700, request({}, { playlist: searchFixtureTracks.map(t => t.id).reverse(), limit: 2 })));
        await lib.removeSource(700, -100123);
        const removed = JSON.parse(await lib.searchLibraryAdvanced(700, request()));
        const other = JSON.parse(await lib.searchLibraryAdvanced(701, request()));
        return { results, patched: patched.map(t => t.id), last: last.id, playlist: playlist.map(t => t.id), expectedOrder: searchFixtureTracks.slice(-2).reverse().map(t => t.id), removed, other: other.length };
    }, { modulePath, tests: cases.cases });
    expect(actual.results).toEqual(cases.cases.map(x => x.expected));
    expect(actual.patched).toEqual([actual.last]);
    expect(actual.playlist).toEqual(actual.expectedOrder);
    expect(actual.removed).toEqual([]);
    expect(actual.other).toBe(1);
});

for (const width of [1440, 390, 320]) {
    test(`instant search and staged filter sheet at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 });
        await seed(page);
        await search(page).fill('night');
        await expect(rows(page)).toHaveCount(4);
        await expect(page.locator('.search-result-summary')).toContainText('4 matches shown');
        await fs.mkdir('artifacts/search', { recursive: true });
        await page.screenshot({ path: `artifacts/search/instant-${width}.png` });
        await page.getByRole('button', { name: /^Filters \(\d+\)$/ }).click();
        await page.getByLabel('Artist', { exact: true }).fill('fixture');
        await page.getByLabel('Min duration', { exact: true }).fill('3:00');
        await page.getByLabel('Max duration', { exact: true }).fill('4:00');
        await expect(rows(page)).toHaveCount(4);
        await expect(page.getByRole('dialog', { name: 'Filters', exact: true })).toBeVisible();
        await page.screenshot({ path: `artifacts/search/filter-sheet-${width}.png` });
        await page.getByLabel('Min duration', { exact: true }).fill('5:00');
        await expect(page.getByRole('alert')).toContainText('Minimum duration must not exceed');
        await expect(rows(page)).toHaveCount(4);
        await expect(page.getByRole('button', { name: 'Show results', exact: true })).toBeDisabled();
        await page.getByLabel('Min duration', { exact: true }).fill('-1');
        await expect(page.getByRole('alert')).toContainText('nonnegative seconds');
        await page.getByLabel('Min duration', { exact: true }).fill('3:00');
        await expect(page.getByRole('alert')).toHaveCount(0);
        await page.getByRole('button', { name: 'Show results', exact: true }).click();
        await expect(titles(page)).toHaveText(['Night Drive']);
        await expect(page.getByRole('button', { name: 'Filters (2)', exact: true })).toBeVisible();
        await expect(page.getByLabel('Artist', { exact: true })).toBeHidden();
        await expect(page.getByRole('button', { name: 'Remove artist filter' })).toBeVisible();
        await page.getByRole('button', { name: 'Clear search', exact: true }).click();
        await expect(titles(page)).toHaveText(['Night Drive']);
        await search(page).fill('night');
        await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
        await expect(search(page)).toHaveValue('night');
        await expect(rows(page)).toHaveCount(4);
        await selectChannel(library(page), '-100123');
        await expect(page.getByRole('button', { name: 'Remove source filter' })).toBeVisible();
        await page.getByRole('button', { name: 'Remove source filter' }).click();
        await expect(page.getByRole('button', { name: 'Remove source filter' })).toHaveCount(0);
        await library(page).getByRole('button', { name: 'Liked Songs', exact: true }).click();
        await expect(search(page)).toHaveValue('night');
        await expect(titles(page)).toHaveText(['Night Drive']);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    });
}

test('typing debounces, Enter submits immediately, and IME waits for composition end', async ({ page }) => {
    await seed(page);
    await page.evaluate(() => fixtureSearchRequests.length = 0);
    await search(page).pressSequentially('night', { delay: 15 });
    await expect(rows(page)).toHaveCount(4);
    expect(await page.evaluate(() => fixtureSearchRequests)).toEqual(['night']);
    await page.evaluate(() => fixtureSearchRequests.length = 0);
    await search(page).dispatchEvent('compositionstart');
    await search(page).fill('شب');
    // Deliberately exceed debounce to prove composition does not search midway.
    await page.waitForTimeout(400);
    expect(await page.evaluate(() => fixtureSearchRequests)).toEqual([]);
    await search(page).dispatchEvent('compositionend');
    await expect(titles(page)).toHaveText(['شب آرام']);
    await search(page).fill('drive night');
    await search(page).press('Enter');
    await expect(titles(page)).toHaveText(['Night Drive', 'Night']);
});

test('superseded reads and errors cannot overwrite newer results or loading state', async ({ page }) => {
    await seed(page);
    await page.evaluate(() => fixtureHoldOperation('searchLibrary', 'night', 'after'));
    await search(page).fill('night');
    await pending(page);
    await search(page).fill('cafe');
    await expect(titles(page)).toHaveText(['Night Drive', 'Café Live']);
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary', true));
    await expect(page.locator('.music-error')).toHaveCount(0);
    await expect(titles(page)).toHaveText(['Night Drive', 'Café Live']);
    await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'false');
    await page.evaluate(() => fixtureHoldOperation('searchLibrary', 'missing', 'before'));
    await search(page).fill('missing');
    await pending(page);
    await expect(rows(page)).toHaveCount(2);
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary', true));
    await page.getByRole('button', { name: 'Retry loading music' }).click();
    await expect(page.getByRole('heading', { name: 'No matches just yet' })).toBeVisible();
});

test('a successful older query cannot publish while its replacement is pending', async ({ page }) => {
    await seed(page);
    await page.evaluate(() => {
        fixtureHoldOperation('searchLibrary', 'night', 'after');
        fixtureHoldOperation('searchLibrary', 'cafe', 'after');
    });
    await search(page).fill('night');
    await search(page).press('Enter');
    await pending(page);
    await search(page).fill('cafe');
    await search(page).press('Enter');
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('searchLibrary'))).toBe(2);
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary'));
    await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'true');
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary'));
    await expect(titles(page)).toHaveText(['Night Drive', 'Café Live']);
    await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'false');
});

test('publication dates use inclusive local days and reversed dates keep valid results', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await seed(page);
    await page.getByRole('button', { name: /^Filters \(\d+\)$/ }).click();
    await page.getByLabel('Published from', { exact: true }).fill('2026-10-28');
    await page.getByLabel('Published to', { exact: true }).fill('2026-10-28');
    await page.getByRole('button', { name: 'Show results', exact: true }).click();
    const expected = await page.evaluate(samples => samples.filter(x => {
        const date = new Date(x.publishedAt);
        return date.getFullYear() === 2026 && date.getMonth() === 9 && date.getDate() === 28;
    }).map(x => x.title), cases.tracks);
    await expect(titles(page)).toHaveText(expected);
    await page.getByRole('button', { name: /^Filters \(\d+\)$/ }).click();
    await page.getByLabel('Published from', { exact: true }).fill('2026-10-29');
    await expect(page.getByRole('alert')).toContainText('Published from must not follow');
    await expect(titles(page)).toHaveText(expected);
    await page.getByLabel('Published from', { exact: true }).fill('0001-01-01');
    await expect(page.getByRole('alert')).toContainText('Enter a valid publication date');
    await expect(page.getByRole('button', { name: 'Show results', exact: true })).toBeDisabled();
});

test('search and filters preserve playback and offline uses the same criteria', async ({ page }) => {
    await seed(page);
    await clickTrackAction(rows(page).first(), 'Save offline');
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    await expect(rows(page).first().getByRole('button', { name: 'Play', exact: true })).toBeVisible();
    await clickTrackAction(rows(page).first(), 'Play');
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    await expect(player).toContainText('Night Drive');
    await search(page).fill('missing');
    await expect(rows(page)).toHaveCount(0);
    await expect(player).toContainText('Night Drive');
    await library(page).getByRole('button', { name: 'Offline', exact: true }).click();
    await expect(rows(page)).toHaveCount(0);
    await search(page).fill('drive night');
    await expect(titles(page)).toHaveText(['Night Drive']);
    await expect(player).toContainText('Night Drive');
});

test('keyboard filters, navigation reset and sign-out discard pending searches', async ({ page }) => {
    await seed(page);
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await expect(library(page).getByRole('heading', { name: 'Find your next listen.' })).toBeVisible();
    await search(page).fill('night');
    await expect(rows(page)).toHaveCount(4);
    const toggle = page.getByRole('button', { name: /^Filters \(\d+\)$/ });
    await toggle.focus(); await page.keyboard.press('Enter');
    await page.getByLabel('Title', { exact: true }).fill('"night drive"');
    await expect(rows(page)).toHaveCount(4);
    await page.getByLabel('Title', { exact: true }).press('Enter');
    await expect(titles(page)).toHaveText(['Night Drive']);
    await expect(toggle).toBeFocused();
    await expect(page.getByRole('button', { name: 'Remove title filter' })).toBeVisible();
    await page.getByRole('button', { name: 'Home', exact: true }).click();
    await openRoot(page, 'Library');
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await expect(search(page)).toHaveValue('');
    await expect(page.getByRole('button', { name: 'Remove title filter' })).toHaveCount(0);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await page.evaluate(() => fixtureHoldOperation('searchLibrary', 'night', 'after'));
    await search(page).fill('night'); await pending(page);
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely' })).toBeVisible();
    await page.evaluate(() => fixtureReleaseOperation('searchLibrary', true));
    await expect(rows(page)).toHaveCount(0);
    await openRoot(page, 'Library');
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await expect(search(page)).toHaveValue('');
    const accounts = await page.evaluate(async modulePath => {
        const lib = await import(modulePath);
        return [JSON.parse(await lib.searchLibrary(700, '', 0, 100)).length, JSON.parse(await lib.searchLibrary(701, '', 0, 100)).length];
    }, modulePath);
    expect(accounts).toEqual([0, 1]);
});

test('filter drafts apply atomically and Reset or dismissal preserve the current search', async ({ page }) => {
    await seed(page);
    await search(page).fill('night');
    await expect(rows(page)).toHaveCount(4);
    const toggle = page.getByRole('button', { name: /^Filters \(\d+\)$/ });
    const sheet = page.getByRole('dialog', { name: 'Filters', exact: true });
    await toggle.click();
    await sheet.getByLabel('Artist', { exact: true }).fill('fixture');
    await sheet.getByRole('button', { name: '3:00 to 5:00', exact: true }).click();
    await expect(sheet.locator('.filter-sheet-summary')).toContainText('2 filters selected');
    await expect(rows(page)).toHaveCount(4);
    await page.keyboard.press('Escape');
    await expect(sheet).toBeHidden();
    await expect(toggle).toBeFocused();
    await expect(toggle).toHaveText('Filters (0)');
    await toggle.click();
    await expect(sheet.getByLabel('Artist', { exact: true })).toHaveValue('');
    await sheet.getByLabel('Artist', { exact: true }).fill('fixture');
    await sheet.getByRole('button', { name: '3:00 to 5:00', exact: true }).click();
    await sheet.getByRole('button', { name: 'Show results', exact: true }).click();
    await expect(titles(page)).toHaveText(['Night Drive', 'Night Walk']);
    await expect(toggle).toHaveText('Filters (2)');
    await toggle.click();
    await sheet.getByRole('button', { name: 'Reset', exact: true }).click();
    await expect(sheet.locator('.filter-sheet-summary')).toContainText('No filters selected');
    await expect(sheet.getByLabel('Artist', { exact: true })).toHaveValue('');
    await sheet.getByRole('button', { name: 'Close filters', exact: true }).click();
    await expect(toggle).toHaveText('Filters (2)');
    await expect(rows(page)).toHaveCount(2);
    await toggle.click();
    await sheet.getByRole('button', { name: 'Reset', exact: true }).click();
    await sheet.getByRole('button', { name: 'Show results', exact: true }).click();
    await expect(rows(page)).toHaveCount(4);
    await expect(search(page)).toHaveValue('night');
    await expect(toggle).toHaveText('Filters (0)');
    await toggle.click();
    await sheet.getByLabel('Artist', { exact: true }).fill('discard this');
    await page.mouse.click(4, 4);
    await expect(sheet).toBeHidden();
    await expect(toggle).toBeFocused();
});

test('phone filter sheet has a reachable footer, scrollable fields and swipe or Back dismissal', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 700 });
    await seed(page);
    const toggle = page.getByRole('button', { name: /^Filters \(\d+\)$/ });
    const sheet = page.getByRole('dialog', { name: 'Filters', exact: true });
    await toggle.click();
    const box = await sheet.boundingBox();
    expect(box.x).toBe(0);
    expect(Math.round(box.y + box.height)).toBe(700);
    await expect(sheet.getByRole('button', { name: 'Show results', exact: true })).toBeInViewport();
    await sheet.getByLabel('Title', { exact: true }).fill('draft');
    await expect(sheet.getByRole('button', { name: 'Reset', exact: true })).toBeInViewport();
    await expect(sheet.getByRole('button', { name: 'Show results', exact: true })).toBeInViewport();
    const grip = await sheet.locator('[data-dialog-handle]').boundingBox();
    await page.mouse.move(grip.x + grip.width / 2, grip.y + grip.height / 2);
    await page.mouse.down();
    await page.mouse.move(grip.x + grip.width / 2, grip.y + grip.height / 2 + 120, { steps: 8 });
    await page.mouse.up();
    await expect(sheet).toBeHidden();
    await expect(toggle).toBeFocused();
    await toggle.click();
    await expect(sheet.getByLabel('Title', { exact: true })).toHaveValue('');
    expect(await page.evaluate(() => channelwaveDismissDialog())).toBe(true);
    await expect(sheet).toBeHidden();
    await expect(toggle).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.classList.contains('modal-open'))).toBe(false);
});

async function measureLiveSearch(page, query, expectedTitle) {
    await page.evaluate(expectedTitle => {
        fixtureSearchTimings.length = 0;
        globalThis.searchLiveTiming = {};
        document.querySelector('#music-search').addEventListener('input', () => { searchLiveTiming.inputAt = performance.now(); }, { once: true });
        const observer = new MutationObserver(() => {
            const titles = [...document.querySelectorAll('.music-track-title > strong')];
            if (titles.length === 1 && titles[0].textContent === expectedTitle) {
                searchLiveTiming.renderedAt = performance.now(); observer.disconnect();
            }
        });
        observer.observe(document.querySelector('.music-tracks'), { childList: true, subtree: true, characterData: true });
    }, expectedTitle);
    await search(page).fill(query);
    await expect(titles(page)).toHaveText([expectedTitle], { timeout: 30000 });
    const live = await page.evaluate(query => ({ ...searchLiveTiming, requests: fixtureSearchTimings.filter(x => x.query === query) }), query);
    const before = live.requests.find(x => x.stage === 'before'), after = live.requests.find(x => x.stage === 'after');
    return {
        liveIncludingDebounceMs: live.renderedAt - live.inputAt,
        inputToAdapterMs: before.at - live.inputAt,
        adapterMs: after.at - before.at,
        adapterToRenderMs: live.renderedAt - after.at
    };
}

test('50,000 tracks return bounded results and record cold, warm and live search latency', async ({ page }, testInfo) => {
    test.setTimeout(180000);
    await seed(page, 50000);
    const timing = await page.evaluate(async modulePath => {
        const lib = await import(modulePath);
        lib.clearSearchCache();
        const request = { criteria: { text: 'synthetic', minDuration: null, maxDuration: null, publishedFrom: null, publishedBefore: null }, terms: ['synthetic'], titleTerms: [], artistTerms: [], favorites: false, chat: null, playlist: null, sort: 0, offset: 0, limit: 101 };
        let started = performance.now();
        const cold = JSON.parse(await lib.searchLibraryAdvanced(700, JSON.stringify(request)));
        const coldMs = performance.now() - started;
        started = performance.now();
        const warm = JSON.parse(await lib.searchLibraryAdvanced(700, JSON.stringify({ ...request, terms: ['49999', 'synthetic'] })));
        return { coldMs, warmMs: performance.now() - started, coldCount: cold.length, warmTitles: warm.map(t => t.title) };
    }, modulePath);
    await fs.mkdir('artifacts/search', { recursive: true });
    await fs.writeFile('artifacts/search/performance.json', JSON.stringify(timing, null, 2));
    timing.firstLive = await measureLiveSearch(page, '49999 synthetic', 'Synthetic 49999');
    timing.subsequentLive = await measureLiveSearch(page, '49998 synthetic', 'Synthetic 49998');
    expect(timing.coldCount).toBe(101);
    expect(timing.warmTitles).toEqual(['Synthetic 49999']);
    await testInfo.attach('search-performance', { body: JSON.stringify(timing, null, 2), contentType: 'application/json' });
    await fs.writeFile('artifacts/search/performance.json', JSON.stringify(timing, null, 2));
});
