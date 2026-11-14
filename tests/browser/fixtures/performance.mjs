import { expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, openLibrary } from './telegram.mjs';
import { installLoadingGates } from './loading.mjs';

export const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const audioBytes = (await fs.readFile(new URL('./stream-tone.mp3', import.meta.url))).length;
export const viewport = page => page.locator('.virtual-tracks');
export const loaded = async (page, count) => expect(viewport(page)).toHaveAttribute('data-loaded-count', String(count));

export async function seedPerformance(page, count = 10000, { notices = false } = {}) {
    await installTelegramFixture(page, 'progressive');
    await installLoadingGates(page);
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled({ timeout: 60000 });
    await page.evaluate(async ({ path, count, notices, audioBytes }) => {
        const lib = await import(path);
        const source = { accountId: 700, chatId: -100123, name: 'Performance fixture', historyComplete: true };
        const tracks = Array.from({ length: count }, (_, i) => ({
            id: `00000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`,
            identity: `700:-100123:${String(i + 1).padStart(6, '0')}`,
            origin: { accountId: 700, chatId: -100123, messageId: i + 1, sourceName: source.name, protected: notices && i % 7 === 0 },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.mp3', mimeType: 'audio/mpeg', size: audioBytes },
            originalTitle: `Performance song ${i + 1}`, originalPerformer: 'Fixture artist', title: `Performance song ${i + 1}`, performer: 'Fixture artist',
            duration: 30, publishedAt: '2026-01-01T00:00:00Z', available: true, favorite: false,
            searchText: `performance song ${i + 1} fixture artist performance fixture`
        }));
        for (let i = 0; i < tracks.length; i += 500) await lib.savePage(JSON.stringify(source), JSON.stringify(tracks.slice(i, i + 500)));
        await lib.saveState(700, 'playlist', JSON.stringify({ Id: '11111111-1111-4111-8111-111111111111', Name: 'Listening list', Tracks: tracks.map(t => t.id) }));
        // Viewport tests need one deterministic sequence across indexing batches.
        // Equal publication dates retain identity order under this explicit sort.
        await lib.saveState(700, 'library-browse-v1', JSON.stringify({ Mode: 'tracks', Group: null, Label: null,
            Order: 6, Criteria: {}, Source: null }));
        const other = { ...source, accountId: 701 };
        await lib.savePage(JSON.stringify(other), JSON.stringify([{ ...tracks[0], id: '22222222-2222-4222-8222-222222222222', identity: '701:-100123:1', title: 'Other account song', origin: { ...tracks[0].origin, accountId: 701 } }]));
        globalThis.performanceFixtureTracks = tracks;
    }, { path: modulePath, count, notices, audioBytes });
    await connectFixture(page);
    await openLibrary(page);
    await page.getByRole('combobox', { name: 'Sort tracks', exact: true }).selectOption('6');
    await loaded(page, Math.min(100, count));
    await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'false');
}

export async function nearEnd(page) {
    await page.locator('#main-content').evaluate(element => { element.scrollTop = element.scrollHeight - element.clientHeight; });
}
