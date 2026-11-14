import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, openLibrary, clickTrackAction } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const dock = page => page.getByRole('region', { name: 'Now playing', exact: true });
async function seed(page, history = true) {
    await installTelegramFixture(page, 'progressive');
    page.on('pageerror', error => console.log('PAGE ERROR:', error.message));
    page.on('console', message => { if (message.type() === 'error') console.log('BROWSER ERROR:', message.text()); });
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeVisible({ timeout: 20000 });
    const bytes = (await fs.readFile(new URL('./fixtures/stream-tone.mp3', import.meta.url))).length;
    await page.evaluate(async ({ modulePath, bytes, history }) => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -100123, name: 'Café Sessions', historyComplete: true };
        const tracks = Array.from({ length: 3 }, (_, i) => ({
            id: crypto.randomUUID(), identity: `700:-100123:${i + 1}`,
            origin: { accountId: 700, chatId: -100123, messageId: i + 1, sourceName: source.name, protected: false },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.mp3', mimeType: 'audio/mpeg', size: bytes },
            originalTitle: `Song ${i + 1}`, originalPerformer: 'Demo artist', title: `Song ${i + 1}`, performer: 'Demo artist',
            duration: 30, publishedAt: '2026-01-01T00:00:00Z', available: true, favorite: false
        }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        globalThis.historyTracks = tracks;
        if (!history) return;
        const start = Date.now() - 120000;
        const reference = { id: tracks[0].id, account: 700, chat: source.chatId, message: 1, mediaKey: 'fixture-unique:' + bytes,
            title: tracks[0].title, artist: 'Demo artist', artistKey: 'demo artist', source: source.name, duration: 180, identity: '700:-100123:1' };
        const events = [40000, 10000].map((time, i) => ({ id: crypto.randomUUID(), account: 700, entryId: crypto.randomUUID(), track: reference,
            started: start + i * 60000, lastActive: start + i * 60000 + time, activeMilliseconds: time, thresholdMilliseconds: 30000,
            qualifiedAt: i === 0 ? start + 30000 : null, startPosition: 0, endPosition: time / 1000, outcome: i === 0 ? 'completed' : 'changed',
            spans: [{ start: start + i * 60000, end: start + i * 60000 + time, activeMilliseconds: time }], offline: false, resumed: false, hasGaps: false, ruleVersion: 1 }));
        const buckets = events.flatMap(e => {
            const hours = new Map();
            for (const span of e.spans) for (let cursor = span.start; cursor < span.end;) {
                const hour = Math.floor(cursor / 3600000) * 3600000, end = Math.min(span.end, hour + 3600000);
                if (!hours.has(hour)) hours.set(hour, []);
                hours.get(hour).push({ start: cursor, end, activeMilliseconds: end - cursor }); cursor = end;
            }
            return [...hours].map(([hour, spans]) => ({ id: String(hour).padStart(16, '0') + ':' + e.id.replaceAll('-', ''), account: 700,
                listenId: e.id, hour, track: e.track, qualifiedAt: e.qualifiedAt != null && Math.floor(e.qualifiedAt / 3600000) * 3600000 === hour ? e.qualifiedAt : null, spans }));
        });
        await lib.commitListening(JSON.stringify({ commit: { account: 700, expectedRevision: 0, generation: 0,
            preferences: { collect: true, retentionDays: 0, rememberPlayer: true }, player: null, events, forgetPlayer: false }, buckets }));
    }, { modulePath, bytes, history });
    await connectFixture(page); await openLibrary(page);
    await expect(page.locator('.track').first()).toBeVisible();
    // Fixture TDLib has no real session filesystem; emulate only its existence.
    await page.evaluate(() => new Promise((resolve, reject) => {
        const request = indexedDB.open('/channelwave-telegram/dbfs', 1);
        request.onsuccess = () => { request.result.close(); resolve(); };
        request.onerror = () => reject(request.error);
    }));
}
async function expectRememberedPlayer(page) {
    const resume = dock(page).getByRole('button', { name: 'Resume playback', exact: true });
    await expect(resume).toBeEnabled();
    await expect(resume).not.toHaveAttribute('title');
    await expect(dock(page).locator('.music-player-status')).toHaveCount(0);
}

async function history(page) {
    await page.locator('.music-content').getByRole('button', { name: 'Open listening activity', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Listening activity', exact: true })).toBeVisible();
    await expect(page.getByText('Loading listening history…', { exact: true })).toHaveCount(0);
}

async function instrumentListeningReads(page) {
    await page.route('**/_content/ChannelWave.Platform.Browser/channelwave.js', async route => {
        const response = await route.fetch();
        let body = await response.text();
        for (const [name, kind] of [['listeningHistory', 'history'], ['listeningBuckets', 'statistics']]) {
            const marker = `export async function ${name}(`;
            const offset = body.indexOf('{', body.indexOf(marker)) + 1;
            body = body.slice(0, offset) + `
                globalThis.listeningReads ??= { history: 0, statistics: 0 };
                globalThis.listeningReads.${kind}++;
                const delay = globalThis.listeningReadDelay || 0;
                if (delay) { await new Promise(resolve => setTimeout(resolve, delay)); globalThis.listeningDelayed${kind} = true; }
                if (globalThis.listeningReadFailure === '${kind}') throw new Error('Synthetic ${kind} read failure');
            ` + body.slice(offset);
        }
        await route.fulfill({ response, body });
    });
}

async function addListens(page, { count = 1, source = -100124, qualified = true, daysAgo = 0 } = {}) {
    await page.evaluate(async ({ path, count, source, qualified, daysAgo }) => {
        const lib = await import(path), state = JSON.parse(await lib.readListeningState(700));
        const start = Date.now() - 3600000 - daysAgo * 86400000;
        const events = Array.from({ length: count }, (_, i) => {
            const time = qualified ? 40000 : 10000, at = start - i * 60000;
            const track = { id: crypto.randomUUID(), account: 700, chat: source, message: i + 100, mediaKey: 'synthetic',
                title: `Archive track ${i + 1}`, artist: 'Archive artist', artistKey: 'archive artist', source: `Archive ${source}`, duration: 180 };
            return { id: crypto.randomUUID(), account: 700, entryId: null, track, started: at, lastActive: at + time,
                activeMilliseconds: time, thresholdMilliseconds: 30000, qualifiedAt: qualified ? at + 30000 : null,
                startPosition: 0, endPosition: time / 1000, outcome: 'completed', spans: [{ start: at, end: at + time, activeMilliseconds: time }], ruleVersion: 1 };
        });
        const buckets = events.flatMap(e => {
            const result = [];
            for (let at = e.started; at < e.lastActive;) {
                const hour = Math.floor(at / 3600000) * 3600000, end = Math.min(e.lastActive, hour + 3600000);
                result.push({ id: String(hour).padStart(16, '0') + ':' + e.id.replaceAll('-', ''), account: 700, listenId: e.id,
                    hour, track: e.track, qualifiedAt: e.qualifiedAt >= at && e.qualifiedAt < end ? e.qualifiedAt : null,
                    spans: [{ start: at, end, activeMilliseconds: end - at }] });
                at = end;
            }
            return result;
        });
        await lib.commitListening(JSON.stringify({ commit: { account: 700, expectedRevision: state.revision, generation: state.generation,
            preferences: state.preferences, player: state.player, events }, buckets }));
    }, { path: modulePath, count, source, qualified, daysAgo });
}

for (const width of [390, 1440]) {
    test(`history statistics and icon actions at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 }); await seed(page); await history(page);
        await expect(page.locator('[data-listen-id]')).toHaveCount(2);
        await fs.mkdir('artifacts/listening', { recursive: true });
        await page.locator('#main-content').evaluate(el => el.scrollTop = 0);
        await page.screenshot({ path: `artifacts/listening/activity-${width}.png` });
        await expect(page.locator('.listening-heard').first()).toContainText('10s');
        await expect(page.locator('.listening-heard').first()).toContainText('Partial');
        expect(await page.locator('.listening-action').evaluateAll(elements => elements.every(e => !e.textContent.trim() && e.getAttribute('aria-label') && e.title))).toBe(true);
        await page.getByRole('group', { name: 'Filter history', exact: true }).getByRole('button', { name: 'Qualified plays', exact: true }).click();
        await expect(page.locator('[data-listen-id]')).toHaveCount(1);
        await page.getByRole('button', { name: 'Clear history filters', exact: true }).click();
        await expect(page.locator('[data-listen-id]')).toHaveCount(2);
        await fs.mkdir('artifacts/listening', { recursive: true });
        await page.screenshot({ path: `artifacts/listening/activity-history-${width}.png` });
        await expect(page.locator('.listening-widget > h2, .listening-widget > header > h2')).toHaveText(['Overview', 'Listening chart', 'Top tracks', 'Playback history']);
        await expect(page.locator('.listening-metrics')).toContainText('50s');
        await expect(page.locator('.listening-metrics dd')).toHaveText(['50s', '1', '1', '1']);
        await page.getByRole('button', { name: 'Show listening activity data table' }).click();
        await expect(page.getByRole('table')).toBeVisible();
        await page.screenshot({ path: `artifacts/listening/activity-data-${width}.png` });
        await page.getByRole('combobox', { name: 'Period', exact: true }).selectOption('30');
        await expect(page.getByText('Minutes heard by week (starting Monday) · Includes partial listens')).toBeVisible();
        await page.screenshot({ path: `artifacts/listening/activity-30-days-${width}.png` });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.getByRole('button', { name: 'Reset filters', exact: true }).click();
        await expect(page.locator('.listening-metrics dd')).toHaveText(['50s', '1', '1', '1']);
        await page.getByRole('button', { name: 'Show listening activity data table' }).click();
        // Keep the normal layout, increasing only viewport height for the full-page export.
        const height = await page.locator('.listening-combined').evaluate(el => Math.ceil(el.scrollHeight));
        await page.setViewportSize({ width, height: height + 300 });
        try {
            await page.locator('.listening-combined').screenshot({ path: `docs/design/listening-experience/activity-${width}.png` });
        } finally { await page.setViewportSize({ width, height: 1000 }); }
    });
}

test('deleting a listen updates statistics and clear respects queue and privacy', async ({ page }) => {
    await seed(page); await history(page);
    await page.getByRole('button', { name: 'Details and actions for Song 1', exact: true }).last().click();
    await page.getByRole('button', { name: 'Delete this listen', exact: true }).click();
    await page.getByRole('button', { name: 'Confirm delete listen', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(1);
    await expect(page.locator('.listening-metrics dd')).toHaveText(['10s', '0', '0', '0']);
    await page.getByRole('button', { name: 'Listening privacy settings', exact: true }).click();
    const settings = page.getByRole('region', { name: 'Listening and privacy', exact: true });
    await settings.getByRole('switch', { name: 'History and statistics', exact: true }).uncheck();
    await expect.poll(() => page.evaluate(async path => JSON.parse(await (await import(path)).readListeningState(700)).preferences.collect, modulePath)).toBe(false);
    await settings.getByRole('button', { name: 'Clear history and statistics', exact: true }).click();
    await page.getByRole('button', { name: 'Confirm clear history and statistics', exact: true }).click();
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).listeningHistory(700, JSON.stringify({ from: 0, to: Number.MAX_SAFE_INTEGER, limit: 50, search: '' }))).items.length, modulePath)).toBe(0);
    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openRoot(page, 'Settings');
    await expect(page.getByRole('switch', { name: 'History and statistics', exact: true })).not.toBeChecked();
});

for (const width of [390, 1440]) {
    test(`restored player and privacy captures at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 }); await seed(page, false);
        await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        await expect(dock(page).getByRole('status')).toContainText('Streaming');
        await dock(page).getByRole('button', { name: 'Pause playback', exact: true }).click();
        await page.reload();
        await expectRememberedPlayer(page);
        await fs.mkdir('artifacts/listening', { recursive: true });
        await page.screenshot({ path: `artifacts/listening/restored-${width}.png` });
        await dock(page).getByRole('button', { name: 'Open full-screen player', exact: true }).click();
        const full = page.getByRole('dialog', { name: 'Music player', exact: true });
        await expect(full.locator('.full-player-status')).toHaveCount(0);
        await expect(full.getByRole('button', { name: 'Resume playback', exact: true })).not.toHaveAttribute('title');
        await expect(full.getByRole('button', { name: 'Mute playback', exact: true })).toBeVisible();
        await page.screenshot({ path: `artifacts/listening/full-player-${width}.png` });
        await full.getByRole('button', { name: 'Minimize player', exact: true }).click();
        await openRoot(page, 'Settings');
        const settings = page.getByRole('region', { name: 'Listening and privacy', exact: true });
        await settings.scrollIntoViewIfNeeded();
        await page.screenshot({ path: `artifacts/listening/privacy-${width}.png` });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    });
}

test('refresh restores duplicate queue entries position volume modes and remains silent', async ({ page }) => {
    await seed(page, false);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(dock(page).getByRole('status')).toContainText('Streaming');
    await clickTrackAction(page.locator('.track').first(), 'Add to queue');
    await dock(page).getByRole('button', { name: 'Pause playback', exact: true }).click();
    const seek = dock(page).getByRole('slider', { name: 'Playback position' });
    await seek.evaluate(el => { el.value = '8'; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); });
    const volume = dock(page).getByRole('slider', { name: 'Volume', exact: true });
    await volume.fill('0.4');
    await expect.poll(async () => page.evaluate(async path => {
        const state = JSON.parse(await (await import(path)).readListeningState(700));
        return state.player ? JSON.parse(state.player.json).position : -1;
    }, modulePath)).toBeGreaterThanOrEqual(7.9);
    const before = await page.evaluate(async path => JSON.parse(JSON.parse(await (await import(path)).readListeningState(700)).player.json), modulePath);
    await page.reload();
    await expectRememberedPlayer(page);
    const after = await page.evaluate(async path => JSON.parse(JSON.parse(await (await import(path)).readListeningState(700)).player.json), modulePath);
    expect(after.entries).toEqual(before.entries); expect(after.currentEntry).toBe(before.currentEntry);
    expect(Number(await dock(page).getByRole('slider', { name: 'Playback position' }).inputValue())).toBeCloseTo(before.position, 2);
    expect(await page.evaluate(() => !globalThis.fixtureAudio || fixtureAudio.paused)).toBe(true);
    await expect(dock(page).getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
    await fs.mkdir('artifacts/listening', { recursive: true }); await page.screenshot({ path: 'artifacts/listening/restored-desktop.png' });
    await dock(page).getByRole('button', { name: 'Resume playback', exact: true }).click();
    await expect(dock(page).getByRole('status')).toContainText('Streaming');
    expect(await page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThanOrEqual(7.9);
});

test('IndexedDB rejects stale commits and foreign events while preserving the other account', async ({ page }) => {
    await seed(page);
    const result = await page.evaluate(async path => {
        const lib = await import(path), state = JSON.parse(await lib.readListeningState(700));
        const commit = { account: 700, expectedRevision: state.revision - 1, generation: state.generation, preferences: state.preferences, player: null, events: [] };
        let rejected = false; try { await lib.commitListening(JSON.stringify({ commit, buckets: [] })); } catch { rejected = true; }
        const own = JSON.parse(await lib.listeningHistory(700, JSON.stringify({ from: 0, to: Number.MAX_SAFE_INTEGER, search: '', limit: 50 })));
        const other = JSON.parse(await lib.listeningHistory(701, JSON.stringify({ from: 0, to: Number.MAX_SAFE_INTEGER, search: '', limit: 50 })));
        return { rejected, own: own.items.length, other: other.items.length };
    }, modulePath);
    expect(result).toEqual({ rejected: true, own: 2, other: 0 });
});

test('actual audio records partial time while seeking and collection pause add nothing', async ({ page }) => {
    await seed(page, false);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(dock(page).getByRole('status')).toContainText('Streaming');
    const read = () => page.evaluate(async path => JSON.parse(await (await import(path)).listeningHistory(700,
        JSON.stringify({ from: 0, to: Number.MAX_SAFE_INTEGER, search: '', limit: 50 }))).items, modulePath);
    await expect.poll(async () => (await read())[0]?.activeMilliseconds ?? 0, { timeout: 12000 }).toBeGreaterThan(1000);
    const seek = dock(page).getByRole('slider', { name: 'Playback position' });
    await seek.evaluate(el => { el.value = '20'; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); });
    await dock(page).getByRole('button', { name: 'Pause playback', exact: true }).click();
    await expect.poll(async () => (await read())[0]?.endPosition ?? 0).toBeGreaterThanOrEqual(19.9);
    const listens = await read(); expect(listens).toHaveLength(1);
    expect(listens[0].activeMilliseconds).toBeLessThan(12000); expect(listens[0].qualifiedAt).toBeNull();
    await openRoot(page, 'Settings');
    await page.getByRole('switch', { name: 'History and statistics', exact: true }).uncheck();
    await dock(page).getByRole('button', { name: 'Resume playback', exact: true }).click();
    await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(21);
    await dock(page).getByRole('button', { name: 'Pause playback', exact: true }).click();
    expect((await read())[0].activeMilliseconds).toBeCloseTo(listens[0].activeMilliseconds, -2);
});

test('renderer termination restores the exclusive owner paused within the checkpoint loss target', async ({ page, context }) => {
    await seed(page, false);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(dock(page).getByRole('status')).toContainText('Streaming');
    await expect.poll(() => page.evaluate(async path => {
        const state = JSON.parse(await (await import(path)).readListeningState(700));
        return state.player ? JSON.parse(state.player.json).position : 0;
    }, modulePath), { timeout: 12000 }).toBeGreaterThan(1);
    const other = await context.newPage(); await other.goto('/');
    const conflict = await other.evaluate(async path => { try { await (await import(path)).openLibrary(); return ''; } catch (error) { return error.message; } }, modulePath);
    expect(conflict).toContain('already open in another tab'); await other.close();
    const current = await page.evaluate(() => fixtureAudio.currentTime);
    const saved = await page.evaluate(async path => JSON.parse(JSON.parse(await (await import(path)).readListeningState(700)).player.json), modulePath);
    const crashed = page.waitForEvent('crash');
    const session = await context.newCDPSession(page);
    session.send('Page.crash').catch(() => {}); await crashed; await page.close();
    const recovered = await context.newPage(); await installTelegramFixture(recovered, 'progressive');
    await recovered.goto('/#view=library');
    await expectRememberedPlayer(recovered);
    const state = await recovered.evaluate(async path => JSON.parse(JSON.parse(await (await import(path)).readListeningState(700)).player.json), modulePath);
    expect(state.currentEntry).toBe(saved.currentEntry); expect(state.entries).toEqual(saved.entries);
    expect(current - state.position).toBeLessThanOrEqual(6);
    expect(await recovered.evaluate(() => !globalThis.fixtureAudio || fixtureAudio.paused)).toBe(true);
    await fs.writeFile('artifacts/listening/renderer-recovery.json', JSON.stringify({ currentBeforeCrash: current, restoredPosition: state.position,
        lostSeconds: current - state.position, checkpoints: 'five seconds', shutdownCallback: false }, null, 2));
    await recovered.close();
});


test('shared source and date filters update every widget while local history filters leave statistics alone', async ({ page }) => {
    await instrumentListeningReads(page); await seed(page);
    await addListens(page); await addListens(page, { source: -100125, qualified: false });
    await addListens(page, { source: -100126, daysAgo: 10 });
    await history(page);
    const metrics = page.locator('.listening-metrics dd'), local = page.getByRole('group', { name: 'Filter history' });
    await expect(metrics).toHaveText(['1m 40s', '2', '2', '2']);
    await expect(page.getByRole('combobox', { name: 'Source', exact: true }).locator('option[value="-100125"]')).toHaveCount(1);
    await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption('-100124');
    await expect(metrics).toHaveText(['40s', '1', '1', '1']);
    await expect(page.locator('[data-listen-id]')).toHaveCount(1);
    await expect(page.locator('.listening-rank-list')).toContainText('Archive -100124');
    await page.getByRole('button', { name: 'Show listening activity data table' }).click();
    await expect(page.getByRole('table')).toContainText('40s');
    const reads = await page.evaluate(() => listeningReads.statistics);
    await local.getByRole('button', { name: 'Partial listens', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(0);
    await expect(metrics).toHaveText(['40s', '1', '1', '1']);
    await page.getByRole('button', { name: 'Clear history filters', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(1);
    await page.getByRole('button', { name: 'Search listening history', exact: true }).click();
    const search = page.getByRole('searchbox', { name: 'Search listening history', exact: true });
    await expect(search).toBeFocused(); await search.fill('no such title');
    await expect(page.getByRole('heading', { name: 'No matching listens', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Close history search', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(1);
    expect(await page.evaluate(() => listeningReads.statistics)).toBe(reads);
    await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption('-100125');
    await expect(metrics).toHaveText(['10s', '0', '0', '0']);
    await expect(page.getByText('No qualified plays in this period.', { exact: true })).toBeVisible();
    await expect(page.locator('[data-listen-id]')).toHaveCount(1);
    await page.getByRole('combobox', { name: 'Period', exact: true }).selectOption('all');
    await expect(page.getByRole('combobox', { name: 'Source', exact: true }).locator('option[value="-100126"]')).toHaveCount(1);
    await page.getByRole('button', { name: 'Reset filters', exact: true }).click();
    await expect(metrics).toHaveText(['1m 40s', '2', '2', '2']);
    await expect(page.getByRole('combobox', { name: 'Period', exact: true })).toHaveValue('7');
    await expect(page.getByRole('combobox', { name: 'Source', exact: true })).toHaveValue('');
    await local.getByRole('button', { name: 'Partial listens', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(2);
    await page.getByRole('combobox', { name: 'Period', exact: true }).selectOption('30');
    await page.getByRole('button', { name: 'Reset filters', exact: true }).click();
    await expect(local.getByRole('button', { name: 'Partial listens', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('[data-listen-id]')).toHaveCount(2);
    await page.getByRole('button', { name: 'Clear history filters', exact: true }).click();
    await expect(page.getByRole('combobox', { name: 'Period', exact: true })).toHaveValue('7');
    await expect(page.locator('[data-listen-id]')).toHaveCount(4);
    await page.getByRole('combobox', { name: 'Period', exact: true }).selectOption('custom');
    await page.getByRole('textbox', { name: 'From', exact: true }).fill('');
    await expect(page.getByRole('alert')).toContainText('Choose both start and end dates');
    await expect(page.locator('[data-listen-id]')).toHaveCount(0);
    await page.getByRole('textbox', { name: 'From', exact: true }).fill('2026-12-31');
    await page.getByRole('textbox', { name: 'Through', exact: true }).fill('2026-01-01');
    await expect(page.getByRole('alert')).toContainText('end date must be on or after');
    await expect(page.locator('[data-listen-id]')).toHaveCount(0);
    await page.getByRole('button', { name: 'Retry listening statistics', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('end date must be on or after');
    const oldDay = await page.evaluate(() => {
        const date = new Date(Date.now() - 10 * 86400000 - 3600000);
        return [date.getFullYear(), String(date.getMonth() + 1).padStart(2, '0'), String(date.getDate()).padStart(2, '0')].join('-');
    });
    await page.getByRole('textbox', { name: 'From', exact: true }).fill(oldDay);
    await page.getByRole('textbox', { name: 'Through', exact: true }).fill(oldDay);
    await expect(metrics).toHaveText(['40s', '1', '1', '1']);
    await expect(page.locator('[data-listen-id]')).toHaveCount(1);
    await page.getByRole('button', { name: 'Reset filters', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(4);
});

test('history pagination does not recalculate statistics and shared filters reset the cursor', async ({ page }) => {
    await instrumentListeningReads(page); await seed(page); await addListens(page, { count: 52 }); await history(page);
    await expect(page.locator('[data-listen-id]')).toHaveCount(50);
    await expect(page.locator('.listening-rank-list > li')).toHaveCount(3);
    await page.getByRole('button', { name: 'Show more ranked music', exact: true }).click();
    await expect(page.locator('.listening-rank-list > li')).toHaveCount(13);
    const reads = await page.evaluate(() => listeningReads.statistics);
    const first = await page.locator('[data-listen-id]').evaluateAll(rows => rows.map(row => row.dataset.listenId));
    await page.getByRole('button', { name: 'Older listens', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(4);
    const older = await page.locator('[data-listen-id]').evaluateAll(rows => rows.map(row => row.dataset.listenId));
    expect(older.some(id => first.includes(id))).toBe(false);
    expect(await page.evaluate(() => listeningReads.statistics)).toBe(reads);
    await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption('-100123');
    await expect(page.locator('[data-listen-id]')).toHaveCount(2);
    await expect(page.getByRole('button', { name: 'Newer listens', exact: true })).toBeDisabled();
});

test('ranking drilldowns reveal history and keep playback and dialog focus actions usable', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 1000 }); await seed(page); await history(page);
    await page.getByRole('combobox', { name: 'Rankings', exact: true }).selectOption('artists');
    await page.getByRole('button', { name: 'Open history for Demo artist', exact: true }).click();
    await expect(page.getByRole('searchbox', { name: 'Search listening history', exact: true })).toBeFocused();
    await expect(page.locator('[data-listen-id]')).toHaveCount(2);
    await page.getByRole('combobox', { name: 'Rankings', exact: true }).selectOption('sources');
    await page.getByRole('button', { name: 'Open history for Café Sessions', exact: true }).click();
    await expect(page.getByRole('combobox', { name: 'Source', exact: true })).toHaveValue('-100123');
    await expect(page.getByRole('heading', { name: 'Playback history', exact: true })).toBeFocused();
    const details = page.getByRole('button', { name: 'Details and actions for Song 1', exact: true }).first();
    await details.click(); await expect(page.getByRole('dialog', { name: 'Listening details', exact: true })).toBeVisible();
    await page.keyboard.press('Escape'); await expect(details).toBeFocused();
    await details.click();
    await page.getByRole('button', { name: 'Show listens for this track', exact: true }).click();
    await expect(page.getByText('Showing listens for the selected track.', { exact: false })).toBeVisible();
    await page.getByRole('button', { name: 'Clear history filters', exact: true }).click();
    await expect(page.getByText('Showing listens for the selected track.', { exact: false })).toHaveCount(0);
    await page.getByRole('combobox', { name: 'Rankings', exact: true }).selectOption('tracks');
    await page.locator('.listening-rankings').getByRole('button', { name: 'Add Song 1 to queue', exact: true }).click();
    await expect(page.locator('.listening-rankings').getByRole('status')).toContainText('Added to queue');
    await page.locator('.listening-history-widget').getByRole('button', { name: 'Play Song 1', exact: true }).first().click();
    await expect(dock(page).getByRole('button', { name: 'Pause playback', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'How listening is counted', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'How listening is counted', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
});

test('statistics and history failures retry independently and rapid shared changes reject stale reads', async ({ page }) => {
    await instrumentListeningReads(page); await seed(page); await addListens(page); await history(page);
    await page.evaluate(() => { globalThis.listeningReadFailure = 'statistics'; });
    await page.getByRole('button', { name: 'Refresh listening activity', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Synthetic statistics read failure');
    await expect(page.locator('[data-listen-id]')).toHaveCount(3);
    await page.evaluate(() => { globalThis.listeningReadFailure = null; });
    const historyReads = await page.evaluate(() => listeningReads.history);
    await page.getByRole('button', { name: 'Retry listening statistics', exact: true }).click();
    await expect(page.getByRole('alert')).toHaveCount(0);
    expect(await page.evaluate(() => listeningReads.history)).toBe(historyReads);
    await page.evaluate(() => { globalThis.listeningReadFailure = 'history'; });
    await page.getByRole('group', { name: 'Filter history' }).getByRole('button', { name: 'Partial listens', exact: true }).click();
    await expect(page.locator('.listening-history-widget').getByRole('alert')).toContainText('Synthetic history read failure');
    await expect(page.locator('.listening-metrics dd')).toHaveText(['1m 30s', '2', '2', '2']);
    await page.evaluate(() => { globalThis.listeningReadFailure = null; });
    await page.getByRole('button', { name: 'Retry loading listening history', exact: true }).click();
    await expect(page.locator('[data-listen-id]')).toHaveCount(1);
    await page.getByRole('button', { name: 'Clear history filters', exact: true }).click();
    const before = await page.evaluate(() => listeningReads.statistics);
    await page.evaluate(() => { globalThis.listeningReadDelay = 1200; });
    await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption('-100124');
    await expect.poll(() => page.evaluate(() => listeningReads.statistics)).toBeGreaterThan(before);
    await page.evaluate(() => { globalThis.listeningReadDelay = 0; });
    await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption('-100123');
    await expect(page.locator('.listening-metrics dd')).toHaveText(['50s', '1', '1', '1']);
    await expect(page.locator('[data-listen-id]')).toHaveCount(2);
    // Wait for the superseded JS promise as well as the current .NET read.
    await expect.poll(() => page.evaluate(() => globalThis.listeningDelayedstatistics)).toBe(true);
    await expect(page.locator('.listening-metrics dd')).toHaveText(['50s', '1', '1', '1']);
    await expect(page.locator('[data-listen-id]')).toHaveCount(2);
    await page.getByRole('button', { name: 'Show listening activity data table' }).click();
    await expect(page.getByRole('table')).toContainText('50s');
});

for (const legacy of ['history', 'statistics']) {
    test(`legacy ${legacy} route reload and Back/Forward normalize without extra history entries`, async ({ page }) => {
        await seed(page);
        await page.addInitScript(() => { globalThis.initialListeningRoute = location.hash; });
        await Promise.all([
            page.waitForEvent('domcontentloaded'),
            page.evaluate(legacy => {
                window.history.replaceState({ channelwave: { index: 0 } }, '', `/#view=${legacy}`);
                // Capture the reload URL before a pending render records the old page.
                window.location.reload();
            }, legacy)
        ]);
        expect(await page.evaluate(() => globalThis.initialListeningRoute)).toBe(`#view=${legacy}`);
        await expect(page.getByRole('heading', { name: 'Listening activity', exact: true })).toBeVisible();
        await expect(page).toHaveURL(/#view=activity$/);
        expect(await page.evaluate(() => window.history.state.channelwave.index)).toBe(0);
        await page.getByRole('button', { name: 'Listening privacy settings', exact: true }).click();
        await expect(page).toHaveURL(/#view=settings$/);
        await page.goBack(); await expect(page).toHaveURL(/#view=activity$/);
        await expect(page.getByRole('combobox', { name: 'Period', exact: true })).toHaveValue('7');
        await page.goForward(); await expect(page).toHaveURL(/#view=settings$/);
    });
}

test('empty and paused collection keep all four widgets visible', async ({ page }) => {
    await seed(page, false); await history(page);
    await expect(page.locator('.listening-widget')).toHaveCount(4);
    await expect(page.locator('.listening-metrics dd')).toHaveText(['0s', '0', '0', '0']);
    await expect(page.getByText('No qualified plays in this period.', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Listening privacy settings', exact: true }).click();
    await page.getByRole('switch', { name: 'History and statistics', exact: true }).uncheck();
    await page.goBack();
    await expect(page.getByRole('status')).toContainText(['History and statistics collection is paused']);
    await expect(page.locator('.listening-widget')).toHaveCount(4);
});


test('switching accounts disposes pending reads and resets all activity filters', async ({ page }) => {
    await instrumentListeningReads(page); await seed(page); await addListens(page); await history(page);
    const before = await page.evaluate(() => listeningReads.statistics);
    await page.evaluate(() => { globalThis.listeningReadDelay = 1500; });
    await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption('-100124');
    await expect.poll(() => page.evaluate(() => listeningReads.statistics)).toBeGreaterThan(before);
    await page.evaluate(() => {
        globalThis.listeningReadDelay = 0;
        fixtureClient.authorize({ '@type': 'authorizationStateWaitPhoneNumber' });
        globalThis.fixtureAccountId = 701;
        fixtureClient.authorize({ '@type': 'authorizationStateReady' });
    });
    await expect(page.getByRole('combobox', { name: 'Source', exact: true })).toHaveValue('');
    await expect(page.getByRole('combobox', { name: 'Period', exact: true })).toHaveValue('7');
    await expect(page.locator('.listening-metrics dd')).toHaveText(['0s', '0', '0', '0']);
    await expect.poll(() => page.evaluate(() => globalThis.listeningDelayedstatistics)).toBe(true);
    await expect(page.locator('[data-listen-id]')).toHaveCount(0);
    await expect(page.locator('.listening-metrics dd')).toHaveText(['0s', '0', '0', '0']);
    await expect(page.getByRole('combobox', { name: 'Source', exact: true }).locator('option[value="-100124"]')).toHaveCount(0);
});
