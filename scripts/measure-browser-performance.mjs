// Run against a Release web host. Fixtures never authenticate or use developer credentials.
import { chromium, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, openLibrary } from '../tests/browser/fixtures/telegram.mjs';

const origin = process.env.CHANNELWAVE_PERF_ORIGIN || 'http://127.0.0.1:5193';
const output = process.env.CHANNELWAVE_PERF_OUTPUT || 'artifacts/performance/after.json';
const repetitions = Number(process.env.CHANNELWAVE_PERF_RUNS || 5);
const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const browser = await chromium.launch({ channel: process.env.CHANNELWAVE_BROWSER || 'msedge', headless: true });
const samples = [];
try {
    for (const width of [390, 1440]) for (let run = 0; run < repetitions; run++) {
        const context = await browser.newContext({ viewport: { width, height: 900 }, serviceWorkers: 'block' });
        const page = await context.newPage();
        await installTelegramFixture(page);
        await page.goto(origin);
        await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled({ timeout: 60000 });
        const coldShellMs = await page.evaluate(() => performance.now());
        await page.evaluate(async path => {
            const lib = await import(path);
            const source = { accountId: 700, chatId: -100123, name: 'Performance fixture', historyComplete: true };
            const tracks = Array.from({ length: 10000 }, (_, i) => ({
                id: `00000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`,
                identity: `700:-100123:${String(i + 1).padStart(6, '0')}`,
                origin: { accountId: 700, chatId: -100123, messageId: i + 1, sourceName: source.name, protected: false },
                media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.wav', mimeType: 'audio/wav', size: 80044 },
                originalTitle: `Performance song ${i + 1}`, originalPerformer: 'Fixture artist', title: `Performance song ${i + 1}`, performer: 'Fixture artist',
                duration: 5, publishedAt: '2026-01-01T00:00:00Z', available: true, favorite: false,
                searchText: `performance song ${i + 1} fixture artist performance fixture`
            }));
            for (let i = 0; i < tracks.length; i += 500) await lib.savePage(JSON.stringify(source), JSON.stringify(tracks.slice(i, i + 500)));
        }, new URL(modulePath, origin).href);
        const firstStarted = Date.now();
        await connectFixture(page);
        await openLibrary(page);
        await expect(page.locator('.track').first()).toBeVisible();
        await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'false');
        const firstLibraryMs = Date.now() - firstStarted;
        for (let i = 0; i < 4; i++) {
            const load = page.getByRole('button', { name: 'Load more tracks', exact: true });
            // Invoke the same paging action without scrolling to its fallback
            // button, which would also trigger the automatic end-of-list loader.
            await load.evaluate(element => element.click());
            if (await page.locator('.virtual-tracks').count())
                await expect(page.locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', String((i + 2) * 100));
            await expect(page.locator('.music-results')).toHaveAttribute('aria-busy', 'false');
        }
        await page.locator('#main-content').evaluate(el => { el.scrollTop = 0; });
        const scroll = await page.evaluate(async () => {
            const panel = document.getElementById('main-content');
            const frames = [], longTasks = [];
            const observer = new PerformanceObserver(list => longTasks.push(...list.getEntries().map(e => e.duration)));
            observer.observe({ type: 'longtask', buffered: false });
            let previous = performance.now();
            for (let i = 0; i < 180; i++) {
                const now = await new Promise(requestAnimationFrame);
                frames.push(now - previous); previous = now;
                panel.scrollTop += i < 90 ? 130 : -130;
            }
            observer.disconnect(); frames.sort((a, b) => a - b);
            return { p95FrameMs: frames[Math.floor(frames.length * .95)], longTasks, mountedRows: document.querySelectorAll('.track').length };
        });
        await page.locator('#main-content').evaluate(el => { el.scrollTop = 0; });
        const search = page.getByRole('searchbox', { name: 'Search your indexed library' });
        const searches = [];
        for (const number of [9999, 9998]) {
            const started = Date.now();
            await search.fill(`performance song ${number}`);
            await expect(page.locator('.music-track-title > strong')).toHaveText([`Performance song ${number}`], { timeout: 30000 });
            searches.push(Date.now() - started);
        }
        const cdp = await context.newCDPSession(page);
        await cdp.send('Performance.enable');
        const { metrics } = await cdp.send('Performance.getMetrics');
        const memory = Object.fromEntries(metrics.filter(m => ['JSHeapUsedSize', 'Nodes'].includes(m.name)).map(m => [m.name, m.value]));
        await page.reload();
        await expect(page.locator('.app-shell')).toBeVisible({ timeout: 60000 });
        const warmShellMs = await page.evaluate(() => performance.now());
        const sample = { width, run: run + 1, coldShellMs, warmShellMs, firstLibraryMs, coldSearchMs: searches[0], warmSearchMs: searches[1], ...scroll, ...memory };
        samples.push(sample);
        process.stdout.write(JSON.stringify(sample) + '\n');
        await context.close();
    }
} finally {
    await browser.close();
    await fs.mkdir(new URL('../artifacts/performance/', import.meta.url), { recursive: true });
    await fs.writeFile(output, JSON.stringify({ origin, configuration: 'Release', tracks: 10000, samples }, null, 2));
}
