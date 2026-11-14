import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { seed } from './fixtures/redesign.mjs';
import { openCategory } from './fixtures/telegram.mjs';

for (const width of [320, 390, 820, 1440]) {
    test(`artist and album lists stay compact and switch to a real grid at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await seed(page);
        await page.evaluate(async () => {
            const lib = await import('/_content/ChannelWave.Platform.Browser/channelwave.js');
            const tracks = discoveryTracks.map((track, index) => ({ ...track,
                performer: index % 2 ? 'هنرمند با نام طولانی برای بررسی چیدمان فهرست موسیقی' : 'A very long artist name for checking compact library rows'
            }));
            await lib.savePage(JSON.stringify({ accountId: 700, chatId: -100123, name: 'Fixture source', historyComplete: true }), JSON.stringify(tracks));
        });
        await fs.mkdir('artifacts/group-layout', { recursive: true });
        for (const category of ['Artists', 'Albums']) {
            await openCategory(page, category);
            const groups = page.locator('.discovery-groups');
            const rows = groups.locator('.discovery-group');
            await expect(rows).toHaveCount(2);
            await page.getByRole('button', { name: 'List view', exact: true }).click();
            await expect(groups).toHaveClass(/groups-list/);
            await expect(async () => {
                const layout = await rows.evaluateAll(rows => rows.map(row => {
                    const box = el => { const r = el.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, width: r.width, height: r.height }; };
                    return { row: box(row), art: box(row.querySelector('.music-art, .discovery-group-fallback')),
                        label: box(row.querySelector('.discovery-group-label')), pin: box(row.querySelector('.group-pin')) };
                }));
                for (const item of layout) {
                    expect(item.row.height).toBeLessThan(110);
                    expect(item.art.width).toBe(48);
                    expect(item.art.height).toBe(48);
                    expect(item.label.x).toBeGreaterThanOrEqual(item.art.right);
                    expect(item.label.right).toBeLessThanOrEqual(item.pin.x);
                    expect(item.pin.right).toBeLessThanOrEqual(item.row.right);
                }
                expect(layout[0].row.x).toBe(layout[1].row.x);
                expect(layout[1].row.y).toBeGreaterThan(layout[0].row.y);
            }).toPass();
            await expect(rows.first()).toContainText('6 tracks');
            expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
            await page.screenshot({ path: `artifacts/group-layout/${category.toLowerCase()}-list-${width}.png` });

            await page.getByRole('button', { name: 'Grid view', exact: true }).click();
            await expect(groups).toHaveClass(/virtual-grid/);
            const cards = await rows.evaluateAll(rows => rows.map(row => {
                const r = row.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width };
            }));
            expect(cards[1].x).toBeGreaterThan(cards[0].x);
            expect(cards[1].y).toBe(cards[0].y);
            await page.screenshot({ path: `artifacts/group-layout/${category.toLowerCase()}-grid-${width}.png` });
            await rows.first().getByRole('button', { name: /^Open (artist|album) / }).click();
            await expect(page.locator('.music-track').first()).toBeVisible();
        }
    });
}
