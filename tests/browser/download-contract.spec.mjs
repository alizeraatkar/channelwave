import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { savePreparedExport, installTelegramFixture, openLibrary, clickTrackAction } from './fixtures/telegram.mjs';

const upstream = await fs.readFile(new URL('../../vendor/tdweb-reference.js', import.meta.url), 'utf8');
const fileManager = upstream.slice(upstream.indexOf('class ListNode'), upstream.indexOf('export default TdClient;'));

async function setup(page, mode) {
    await installTelegramFixture(page);
    // Exercise the checksum-pinned upstream FileManager, not a replacement readFile implementation.
    await page.addInitScript(`(() => {
        const log = { debug() {}, info() {}, warn() {}, error() {} };
        ${fileManager}
        globalThis.ContractFileManager = FileManager;
    })();`);
    await page.addInitScript(mode => {
        const original = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            if (query['@type'] === 'getMessage' && this.contractFiles) {
                const message = await original.call(this, query);
                message.content.audio.audio.local.is_downloading_completed = true;
                return message;
            }
            if (query['@type'] !== 'readFile') return original.call(this, query);
            const raw = await original.call(this, query);
            if (mode === 'truncated') return { ...raw, data: raw.data.slice(0, 1024) };
            if (!this.contractFiles) {
                const bytes = new Uint8Array(await (await original.call(this, { '@type': 'readFile', file_id: 1 })).data.arrayBuffer());
                const file = { id: 1, local: { is_downloading_completed: true } };
                this.contractFiles = new ContractFileManager('fixture-cache', this);
                this.contractFiles.isInited = true;
                if (mode === 'memory') this.contractFiles.registerFile({ ...file, arr: bytes });
                else {
                    const db = await new Promise((resolve, reject) => {
                        const request = indexedDB.open('fixture-cache', 1);
                        request.onupgradeneeded = () => request.result.createObjectStore('keyvaluepairs');
                        request.onsuccess = () => resolve(request.result);
                        request.onerror = () => reject(request.error);
                    });
                    await new Promise((resolve, reject) => {
                        const tx = db.transaction('keyvaluepairs', 'readwrite');
                        tx.objectStore('keyvaluepairs').put(new Blob([bytes]), 'audio');
                        tx.oncomplete = resolve; tx.onerror = reject;
                    });
                    this.contractFiles.idb = Promise.resolve(db);
                    this.contractFiles.registerFile({ ...file, idb_key: 'audio' });
                }
            }
            return this.contractFiles.readFile(query);
        };
    }, mode);
    await page.goto('/');
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await page.getByRole('button', { name: 'Add to library: :)', exact: true }).click();
    await openLibrary(page);
}

for (const mode of ['memory', 'persistent']) {
    test(`Download exports exact bytes from the pinned tdweb ${mode} cache`, async ({ page }) => {
        await setup(page, mode);
        await clickTrackAction(page.locator('.track'), 'Download');
        await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
        const pending = page.waitForEvent('download');
        await page.evaluate(() => completeFixtureDownload());
        await savePreparedExport(page);
        const download = await pending;
        const bytes = await fs.readFile(await download.path());
        expect(bytes.length).toBe(80044);
        expect(bytes.toString('ascii', 0, 4)).toBe('RIFF');
        await expect(page.getByRole('alert')).toHaveCount(0);
        // A second export must reuse the completed file rather than start another transfer.
        const repeated = page.waitForEvent('download');
        await clickTrackAction(page.locator('.track'), 'Download');
        await savePreparedExport(page);
        expect(await fs.readFile(await (await repeated).path())).toEqual(bytes);
        expect(await page.evaluate(() => fixtureCalls.filter(q => q === 'downloadFile').length)).toBe(1);
    });
}

test('Download rejects nonempty but truncated audio without claiming success', async ({ page }) => {
    await setup(page, 'truncated');
    let exports = 0; page.on('download', () => exports++);
    await clickTrackAction(page.locator('.track'), 'Download');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.getByRole('alert')).toContainText('1024 of 80044');
    await expect(page.locator('.music-feedback')).toHaveCount(0);
    await expect(page.locator('.track').getByRole('button', { name: 'Download', exact: true, includeHidden: true })).toBeEnabled();
    expect(exports).toBe(0);
});
