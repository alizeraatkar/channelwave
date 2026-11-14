import { expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, openLibrary } from './telegram.mjs';
const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const main = page => page.locator('.music-content');
export async function seed(page, count = 12) {
    await installTelegramFixture(page, 'progressive');
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled({ timeout: 20000 });
    const bytes = (await fs.stat(new URL('./stream-tone.mp3', import.meta.url))).size;
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
