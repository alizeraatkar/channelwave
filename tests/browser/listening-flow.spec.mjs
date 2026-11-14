import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { installTelegramFixture, connectFixture, addFixtureSource, openLibrary, clickTrackAction, openChannels } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const bytes = (await fs.readFile(new URL('./fixtures/stream-tone.mp3', import.meta.url))).length;
const dock = page => page.getByRole('region', { name: 'Now playing', exact: true });
const queue = page => page.getByRole('dialog', { name: 'Up next', exact: true });
async function queueAction(page, entry, action) {
    const row = queue(page).locator('.queue-list > li').nth(entry - 1);
    const button = row.getByRole('button', { name: `${action} queue entry ${entry}${action === 'Move' ? ' down' : ''}`, exact: true });
    if (!await button.isVisible()) await row.locator('summary').click();
    await button.click();
}

async function seed(page, count = 4) {
    await installTelegramFixture(page, 'progressive');
    await page.goto('/');
    await page.evaluate(async ({ modulePath, bytes, count }) => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -100123, name: 'Café Sessions', historyComplete: true };
        const tracks = Array.from({ length: count }, (_, index) => ({
            id: crypto.randomUUID(), identity: `700:-100123:${String(index + 1).padStart(6, '0')}`,
            origin: { accountId: 700, chatId: -100123, messageId: index + 1, sourceName: source.name, protected: false },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.mp3', mimeType: 'audio/mpeg', size: bytes },
            originalTitle: `Song ${String(index + 1).padStart(3, '0')}`, originalPerformer: 'Demo artist', title: `Song ${String(index + 1).padStart(3, '0')}`,
            performer: 'Demo artist', duration: 30, publishedAt: '2026-01-01T00:00:00+00:00', available: true, favorite: false,
            searchText: `song ${String(index + 1).padStart(3, '0')} demo artist café sessions`
        }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        globalThis.listeningTracks = tracks;
    }, { modulePath, bytes, count });
    await connectFixture(page); await openLibrary(page);
    await expect(page.locator('.track').first()).toBeVisible();
}

test('Android picker groups outputs and shows a Cast receiver discovered while open', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await page.route(`**${modulePath}*`, async route => {
        const response = await route.fetch();
        const body = (await response.text()).replace(
            'export async function getAudioOutputs()', 'async function getBrowserAudioOutputs()');
        await route.fulfill({ response, body: body + `
            export async function getAudioOutputs() {
                const browser = await getBrowserAudioOutputs();
                return {
                    ...browser,
                    groupAndroidOutputs: true,
                    picker: 'Open Android audio outputs',
                    help: 'Connect both devices to the same Wi-Fi.',
                    devices: [
                        ...browser.devices,
                        { id: 'android:speaker', name: 'Phone speaker' },
                        { id: 'headphones', name: 'My headphones', bluetooth: true },
                        ...(globalThis.testCastAvailable
                            ? [{ id: 'cast:living-room', name: 'Living room', remote: true }]
                            : [])
                    ]
                };
            }
        ` });
    });
    await seed(page, 1);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await dock(page).getByRole('button', { name: /^Play on…/ }).click();
    const picker = page.getByRole('dialog', { name: 'Play on…' });
    const cast = picker.getByRole('region', { name: 'Google Cast' });
    await expect(picker.getByRole('button', { name: 'Open Android audio outputs' })).toHaveCount(0);
    await expect(picker.getByRole('region', { name: 'On this device' })
        .getByRole('button', { name: 'Phone speaker' })).toBeVisible();
    await expect(picker.getByRole('region', { name: 'Bluetooth' })
        .getByRole('button', { name: 'My headphones' })).toBeVisible();
    await expect(cast).toContainText('No Cast devices found');
    await page.evaluate(() => { globalThis.testCastAvailable = true; });
    await expect(cast.getByRole('button', { name: 'Living room' })).toBeVisible();
    await expect(cast.getByText('No Cast devices found', { exact: false })).toHaveCount(0);
});

for (const width of [390, 1440]) {
    test(`audio output selection preserves playback and pauses on device removal at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await page.addInitScript(() => {
            const speakers = { kind: 'audiooutput', deviceId: 'desk-speakers', label: 'Desk speakers' };
            let devices = [speakers];
            Object.defineProperty(HTMLMediaElement.prototype, 'sinkId', { configurable: true, get() { return this.selectedSink ?? ''; } });
            Object.defineProperty(HTMLMediaElement.prototype, 'setSinkId', {
                configurable: true, value: async function (id) { this.selectedSink = id; }
            });
            navigator.mediaDevices.enumerateDevices = async () => devices;
            navigator.mediaDevices.selectAudioOutput = async () => { throw new DOMException('Cancelled', 'AbortError'); };
            globalThis.removeTestAudioOutput = () => {
                devices = [];
                navigator.mediaDevices.dispatchEvent(new Event('devicechange'));
            };
        });
        await seed(page, 1);
        await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        await expect(dock(page).getByRole('button', { name: 'Pause playback', exact: true })).toBeVisible();
        const trigger = dock(page).getByRole('button', { name: /^Play on…/ });
        await trigger.click();
        const picker = page.getByRole('dialog', { name: 'Play on…' });
        await picker.getByRole('button', { name: 'Choose another output' }).click();
        await expect(picker.getByRole('alert')).toHaveCount(0);
        await picker.getByRole('button', { name: 'Desk speakers' }).click();
        await expect(picker.getByRole('button', { name: 'Desk speakers Selected' })).toHaveAttribute('aria-pressed', 'true');
        await page.keyboard.press('Escape');
        await expect(trigger).toHaveAccessibleName('Play on… Current output: Desk speakers');
        await expect(dock(page).getByRole('button', { name: 'Pause playback', exact: true })).toBeVisible();
        await dock(page).getByRole('button', { name: 'Open full-screen player', exact: true }).click();
        const fullPlayer = page.getByRole('dialog', { name: 'Music player', exact: true });
        await fullPlayer.getByRole('button', { name: 'Play on… Current output: Desk speakers' }).click();
        await expect(picker.getByRole('button', { name: 'Desk speakers Selected' })).toHaveAttribute('aria-pressed', 'true');
        await page.keyboard.press('Escape');
        await expect(fullPlayer).toBeVisible();
        await page.keyboard.press('Escape');
        await page.evaluate(() => globalThis.removeTestAudioOutput());
        await expect(trigger).toHaveAccessibleName('Play on… Current output: System default');
        await expect(dock(page).getByRole('button', { name: 'Resume playback', exact: true })).toBeVisible();
    });

    test(`queue edits use full collection and preserve playback at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 }); await seed(page, 130);
        await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
        await expect(dock(page).getByRole('status')).toContainText('Streaming from Telegram');
        await dock(page).getByRole('button', { name: 'Pause playback', exact: true }).click();
        await clickTrackAction(page.locator('.track').nth(2), 'Play next');
        await dock(page).getByRole('button', { name: 'Open Up next', exact: true }).click();
        await expect(queue(page).locator('.queue-list-heading')).toContainText('130');
        await expect(queue(page).locator('.queue-list > li')).toHaveCount(40);
        await expect(queue(page).locator('.queue-current .queue-source')).toHaveText('Café Sessions');
        await expect(queue(page).locator('.queue-list > li').first().locator('.queue-source')).toHaveText('Café Sessions');
        await expect(queue(page).locator('.queue-list > li').first()).toContainText('Song 003');
        await queueAction(page, 1, 'Move');
        await expect(queue(page).locator('.queue-list > li').nth(1)).toContainText('Song 003');
        await queueAction(page, 2, 'Remove');
        await expect(queue(page).locator('.queue-list-heading')).toContainText('129');
        await expect(queue(page).locator('.queue-current')).toContainText('Song 001');
        await expect(queue(page).getByText('Loading queue…', { exact: true })).toHaveCount(0);
        await queue(page).getByRole('button', { name: 'Next songs', exact: true }).click();
        await expect(queue(page).locator('.queue-list > li')).toHaveCount(40);
        await expect(queue(page).locator('.queue-list > li').first()).toContainText('Song 042');
        await queue(page).getByRole('button', { name: 'Previous songs', exact: true }).click();
        await expect(queue(page).locator('.queue-list > li').first()).toContainText('Song 002');
        await fs.mkdir('artifacts/features', { recursive: true });
        await page.screenshot({ path: `artifacts/features/queue-${width}.png` });
        await queue(page).getByRole('button', { name: 'Clear upcoming', exact: true }).click();
        await expect(queue(page).locator('.queue-list > li')).toHaveCount(0);
        await expect(queue(page).locator('.queue-current')).toContainText('Song 001');
        await queue(page).getByRole('button', { name: 'Close up next', exact: true }).click();
        await expect(dock(page)).toContainText('Song 001');
        await expect(dock(page).getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
    });
}

test('queue keeps controls reachable at 320px with enlarged text and long labels', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 568 }); await seed(page, 130);
    await page.evaluate(async p => {
        const lib = await import(p);
        await lib.setLocalMetadata(700, listeningTracks[1].id, 'آهنگ بسیار طولانی — ALongRecordingTitleWithoutSpaces'.repeat(3), 'هنرمند آزمایشی — Long artist name');
    }, modulePath);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(dock(page).getByRole('status')).toContainText('Streaming from Telegram');
    await dock(page).getByRole('button', { name: 'Open Up next', exact: true }).click();
    await expect(queue(page).locator('.queue-list > li')).toHaveCount(40);
    await queue(page).evaluate(dialog => {
        const sizes = [...dialog.querySelectorAll('*')].map(el => [el, parseFloat(getComputedStyle(el).fontSize)]);
        for (const [el, size] of sizes) el.style.fontSize = `${size * 2}px`;
    });
    const geometry = await queue(page).evaluate(dialog => {
        const footer = dialog.querySelector('footer').getBoundingClientRect(), bounds = dialog.getBoundingClientRect();
        return { width: dialog.scrollWidth, available: dialog.clientWidth, footerBottom: footer.bottom, bottom: bounds.bottom };
    });
    expect(geometry.width).toBeLessThanOrEqual(geometry.available + 1);
    expect(geometry.footerBottom).toBeLessThanOrEqual(geometry.bottom + 1);
    await expect(queue(page).getByRole('button', { name: 'Shuffle', exact: true })).toBeVisible();
    await fs.mkdir('artifacts/features', { recursive: true });
    await page.screenshot({ path: 'artifacts/features/queue-320-text-200.png' });
});

test('adding repeated entries without a current song does not autoplay', async ({ page }) => {
    await seed(page);
    await clickTrackAction(page.locator('.track').nth(1), 'Add to queue');
    await clickTrackAction(page.locator('.track').nth(1), 'Add to queue');
    await expect(dock(page)).toHaveCount(0);
    await page.locator('.queued-tracks-button').click();
    await expect(queue(page).locator('.queue-list > li')).toHaveCount(2);
    const ids = await queue(page).locator('[data-queue-entry]').evaluateAll(rows => rows.map(row => row.dataset.queueEntry));
    expect(new Set(ids).size).toBe(2);
    await queueAction(page, 1, 'Remove');
    await expect(queue(page).locator('.queue-list > li')).toHaveCount(1);
    await expect(dock(page)).toHaveCount(0);
});

test('natural completion advances once and playback modes are shared with the full-screen player', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await seed(page);
    await page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(dock(page).getByRole('status')).toContainText('Streaming from Telegram');
    await page.evaluate(() => { fixtureAudio.currentTime = fixtureAudio.duration - 0.1; });
    await expect(dock(page)).toContainText('Song 002');
    await expect(dock(page).getByRole('status')).toContainText('Streaming from Telegram');
    await dock(page).getByRole('button', { name: 'Open full-screen player', exact: true }).click();
    const player = page.getByRole('dialog', { name: 'Music player', exact: true });
    await player.getByRole('button', { name: 'Repeat: off', exact: true }).click();
    await expect(player.getByRole('button', { name: 'Repeat: one', exact: true })).toBeVisible();
    await player.getByRole('button', { name: 'Shuffle', exact: true }).click();
    await expect(player.getByRole('button', { name: 'Shuffle', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await fs.mkdir('artifacts/features', { recursive: true });
    await page.screenshot({ path: 'artifacts/features/player-1440.png' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: 'artifacts/features/player-390.png' });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await player.getByRole('button', { name: 'Minimize player', exact: true }).click();
    await expect(dock(page).getByRole('button', { name: 'Repeat: one', exact: true })).toBeVisible();
});

test('live source updates preserve favorites, ignore cache eviction, and keep deleted songs visible', async ({ page }) => {
    await installTelegramFixture(page, 'progressive'); await page.goto('/');
    await connectFixture(page); await addFixtureSource(page); await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(1);
    const originalKey = await page.locator('.track').first().getAttribute('data-track-key');
    const originalRow = page.locator(`[data-track-key="${originalKey}"]`);
    await originalRow.getByRole('button', { name: 'Favorite track', exact: true }).click();
    await page.evaluate(async () => {
        const original = await fixtureClient.send({ '@type': 'getMessage', chat_id: -100123, message_id: 1 });
        original.chat_id = -100123;
        const added = structuredClone(original); added.id = 2; added.content.audio.title = 'New channel song';
        globalThis.fixtureMessages = { 1: original, 2: added };
        fixtureClient.options.onUpdate({ '@type': 'updateNewMessage', message: added });
    });
    await expect(page.locator('.track')).toHaveCount(2);
    await page.evaluate(() => fixtureClient.options.onUpdate({ '@type': 'updateDeleteMessages', chat_id: -100123, message_ids: [1], is_permanent: true, from_cache: true }));
    await expect(originalRow.getByRole('button', { name: 'Remove favorite', exact: true })).toBeEnabled();
    await page.evaluate(() => {
        delete fixtureMessages[1];
        fixtureClient.options.onUpdate({ '@type': 'updateDeleteMessages', chat_id: -100123, message_ids: [1], is_permanent: true, from_cache: false });
    });
    await expect(originalRow).toContainText('Telegram message unavailable');
    await expect(originalRow.getByRole('button', { name: 'Remove favorite', exact: true })).toBeEnabled();
    await expect(originalRow.getByRole('button', { name: 'Stream', exact: true })).toBeDisabled();
    await openChannels(page);
    await expect(page.getByRole('button', { name: 'Refresh now', exact: true })).toBeVisible();
    await fs.mkdir('artifacts/features', { recursive: true });
    await page.screenshot({ path: 'artifacts/features/sources-1440.png' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: 'artifacts/features/sources-390.png' });
});
