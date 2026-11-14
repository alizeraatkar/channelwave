import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { expectLoadedTracks } from './fixtures/library.mjs';
import fs from 'node:fs/promises';
import { installTelegramFixture, openLibrary, clickTrackAction, connectFixture } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const audioBytes = (await fs.readFile(new URL('./fixtures/stream-tone.mp3', import.meta.url))).length;
const dock = page => page.getByRole('region', { name: 'Now playing', exact: true });
const full = page => page.getByRole('dialog', { name: 'Music player', exact: true });

async function seed(page, { protectedTrack = false, count = 4, palette = null, artworkKind = 0 } = {}) {
    await installTelegramFixture(page, 'progressive');
    await page.goto('/');
    await page.evaluate(async ({ modulePath, audioBytes, protectedTrack, count, palette, artworkKind }) => {
        const lib = await import(modulePath);
        const source = { accountId: 700, chatId: -100123, name: 'Fixture source', historyComplete: true };
        const names = ['Café', 'Night Drive', 'Live session', 'آخر شب — A recording with a long bilingual title'];
        const tracks = Array.from({ length: count }, (_, i) => ({
            id: crypto.randomUUID(), identity: `700:-100123:${String(i + 1).padStart(5, '0')}`,
            origin: { accountId: 700, chatId: -100123, messageId: i + 1, sourceName: source.name, protected: protectedTrack && i === 0 },
            media: { remoteId: 'fixture', uniqueId: 'fixture-unique', runtimeId: 1, fileName: 'fixture.mp3', mimeType: 'audio/mpeg', size: audioBytes },
            originalTitle: names[i % names.length], originalPerformer: 'Fixture artist', title: names[i % names.length], performer: i === 3 ? 'هنرمند آزمایشی' : 'Fixture artist',
            duration: 30, publishedAt: '2026-01-01T00:00:00+00:00', available: true, favorite: false,
            searchText: `${names[i % names.length]} fixture artist fixture source`.toLowerCase()
        }));
        await lib.savePage(JSON.stringify(source), JSON.stringify(tracks));
        // Synthetic covers only: deterministic geometry, never a production catalog asset.
        for (let i = 0; i < Math.min(palette?.length ?? 3, tracks.length); i++) {
            if (tracks[i].origin.protected || palette && !palette[i]) continue;
            const canvas = document.createElement('canvas'); canvas.width = canvas.height = 320;
            const ctx = canvas.getContext('2d');
            ctx.fillStyle = palette ? palette[i] : ['#214234', '#293e50', '#4c3529'][i]; ctx.fillRect(0, 0, 320, 320);
            if (!palette) {
                ctx.fillStyle = ['#cc7850', '#a6bfca', '#d4a66d'][i]; ctx.beginPath(); ctx.arc(180, 125, 94, 0, Math.PI * 2); ctx.fill();
                ctx.fillStyle = ['#152d25', '#1a2c3c', '#35271f'][i]; ctx.beginPath(); ctx.moveTo(0, 210); ctx.lineTo(320, 140); ctx.lineTo(320, 320); ctx.lineTo(0, 320); ctx.fill();
            }
            await lib.setArtwork(700, tracks[i].id, JSON.stringify({ mediaKey: `fixture-unique:${audioBytes}`, kind: artworkKind, data: canvas.toDataURL('image/png').split(',')[1] }));
        }
    }, { modulePath, audioBytes, protectedTrack, count, palette, artworkKind });
    await connectFixture(page);
    await expect(page.getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await openLibrary(page);
    await expectLoadedTracks(page, count);
}

async function start(page, index = 0) {
    await page.locator('.track').nth(index).getByRole('button', { name: 'Stream', exact: true }).click();
    await expect(dock(page).getByRole('status')).toContainText('Streaming from Telegram');
    await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(0);
}

for (const width of [390, 1440]) {
    test(`played wave joins the remaining line at every progress position at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await seed(page); await start(page);
        // Geometry inspection must not race the tone's real natural completion.
        // Automatic next is covered separately by the listening-flow suite.
        await page.evaluate(() => { fixtureAudio.playbackRate = 0.1; });
        for (const expanded of [false, true]) {
            if (expanded) await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
            const player = expanded ? full(page) : dock(page);
            const seek = player.getByRole('slider', { name: 'Playback position' });
            await expect(seek).toBeEnabled();
            await expect(player.locator('.music-seekbar')).toHaveClass(/is-playing/);
            for (const value of [0, 12, 30]) {
                await seek.evaluate((el, value) => { el.value = value; el.dispatchEvent(new Event('input', { bubbles: true })); }, value);
                await expect(seek).toHaveValue(String(value));
                const geometry = await player.locator('.music-seekbar').evaluate(el => {
                    const rect = selector => el.querySelector(selector).getBoundingClientRect();
                    const track = rect('.music-seek-track'), played = rect('.music-seek-played'), remaining = rect('.music-seek-remaining'), buffer = rect('.music-seek-buffer');
                    const input = rect('input');
                    const thumbSize = parseFloat(getComputedStyle(el).getPropertyValue('--seek-thumb-size'));
                    const fraction = Number(el.querySelector('input').value) / Number(el.querySelector('input').max);
                    return {
                        join: played.right - remaining.left,
                        thumb: remaining.left - (input.left + thumbSize / 2 + fraction * (input.width - thumbSize)),
                        center: remaining.top + remaining.height / 2 - (track.top + track.height / 2),
                        bufferedStart: buffer.left - remaining.left,
                        bufferedEnd: buffer.right - track.right,
                        baseline: getComputedStyle(el.querySelector('.music-seek-track')).backgroundImage,
                        playedBackground: getComputedStyle(el.querySelector('.music-seek-played')).backgroundImage
                    };
                });
                expect(Math.abs(geometry.join)).toBeLessThan(1);
                expect(Math.abs(geometry.thumb)).toBeLessThan(1);
                expect(Math.abs(geometry.center)).toBeLessThan(1);
                expect(Math.abs(geometry.bufferedStart)).toBeLessThan(1);
                expect(geometry.bufferedEnd).toBeLessThanOrEqual(1);
                expect(geometry.baseline).toBe('none');
                expect(geometry.playedBackground).toBe('none');
            }
            await seek.evaluate(el => { el.value = 12; el.dispatchEvent(new Event('input', { bubbles: true })); });
            await page.emulateMedia({ reducedMotion: 'reduce' });
            await expect(player.locator('.music-seek-wave')).toHaveCSS('display', 'block');
            await expect(player.locator('.music-seek-wave rect')).toHaveCSS('animation-name', 'none');
            await page.emulateMedia({ reducedMotion: 'no-preference' });
        }
        await full(page).getByRole('button', { name: 'Pause playback', exact: true }).click();
        await expect(full(page).locator('.music-seek-wave')).toHaveCSS('display', 'none');
        await expect(full(page).locator('.music-seek-played')).not.toHaveCSS('background-image', 'none');
    });
}

test('progress updates and player dialogs preserve the library panel position', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page, { count: 25 }); await start(page);
    const panel = page.locator('#main-content');
    await panel.evaluate(el => { el.scrollTop = 650; });
    const before = await panel.evaluate(el => el.scrollTop);
    await page.evaluate(() => { fixtureAudio.currentTime = 12; });
    await expect.poll(() => dock(page).getByRole('slider', { name: 'Playback position' }).inputValue().then(Number)).toBeGreaterThanOrEqual(12);
    expect(await panel.evaluate(el => el.scrollTop)).toBe(before);
    await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
    await full(page).getByRole('button', { name: 'More actions', exact: true }).click();
    await page.keyboard.press('Escape');
    await expect(full(page)).toBeVisible();
    await expect(panel).toHaveCSS('overflow-y', 'hidden');
    await full(page).getByRole('button', { name: 'Minimize player', exact: true }).click();
    await expect(full(page)).toHaveCount(0);
    expect(await panel.evaluate(el => el.scrollTop)).toBe(before);
    expect(await page.evaluate(() => scrollY)).toBe(0);
});

test.beforeEach(async ({ page }) => {
    page.playerErrors = [];
    page.on('pageerror', error => page.playerErrors.push(error.message));
});
test.afterEach(async ({ page }) => {
    expect(page.playerErrors).toEqual([]);
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

const tint = page => full(page).evaluate(el => el.style.getPropertyValue('--player-artwork-color'));

for (const width of [390, 1440]) {
    test(`artwork colors tint only the full-screen player with safe contrast at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 });
        await seed(page, { count: 6, palette: ['#e03030', '#3060e0', '#888888', '#ffff00', '#ffffff', null] });
        await page.locator('.track').first().getByRole('button', { name: 'Open player for Café' }).click();
        for (let i = 0; i < 6; i++) {
            if (i < 4) {
                await expect.poll(() => tint(page)).not.toBe('');
                // Assert the selected hue, not a specific implementation's exact RGB output.
                await expect.poll(async () => {
                    const rgb = (await tint(page)).match(/\d+/g)?.map(Number) ?? [];
                    if (rgb.length !== 3) return false;
                    return i === 0 ? rgb[0] > rgb[1] * 2 && rgb[0] > rgb[2] * 2
                        : i === 1 ? rgb[2] > rgb[0] * 2 && rgb[2] > rgb[1]
                        : i === 2 ? rgb[0] === rgb[1] && rgb[1] === rgb[2]
                        : rgb[0] === rgb[1] && rgb[0] > rgb[2] * 2;
                }).toBe(true);
            } else {
                await expect.poll(() => tint(page)).toBe('');
                await expect(full(page)).toHaveCSS('background-color', 'rgb(22, 22, 22)');
            }
            const contrasts = await full(page).evaluate(el => {
                const rgb = color => color.match(/[\d.]+/g).slice(0, 3).map(Number);
                const lum = values => values.map(v => { const c = v / 255; return c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4; }).reduce((sum, c, j) => sum + c * [.2126, .7152, .0722][j], 0);
                const background = rgb(el.style.getPropertyValue('--player-artwork-color') || 'rgb(22, 22, 22)');
                const contrast = color => (lum(rgb(color)) + .05) / (lum(background) + .05);
                return {
                    luminance: lum(background),
                    text: ['h3', '.full-player-titlebar p', '.full-player-status', '.music-seek-times', '.full-player-source small'].map(selector => contrast(getComputedStyle(el.querySelector(selector)).color)),
                    action: contrast(getComputedStyle(el.querySelector('.full-player-toggle')).backgroundColor),
                    icon: contrast(getComputedStyle(el.querySelector('header > button')).color),
                    outside: document.querySelector('.music-track .music-art').style.getPropertyValue('--player-artwork-color')
                };
            });
            expect(contrasts.luminance).toBeLessThanOrEqual(.0601);
            for (const ratio of contrasts.text) expect(ratio).toBeGreaterThanOrEqual(4.5);
            expect(contrasts.action).toBeGreaterThanOrEqual(3);
            expect(contrasts.icon).toBeGreaterThanOrEqual(3);
            expect(contrasts.outside).toBe('');
            if (i === 0) {
                await expect(full(page)).toHaveCSS('transition-duration', '0.35s');
                await expect.poll(() => full(page).evaluate(el => el.getAnimations().length)).toBe(0);
                await fs.mkdir('artifacts/player-colors', { recursive: true });
                await page.screenshot({ path: `artifacts/player-colors/red-${width}.png` });
            }
            if (i < 5) await full(page).getByRole('button', { name: 'Next track' }).click();
        }
        expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
    });
}

test('player color follows source photos, delayed and replaced artwork, and ignores stale images', async ({ page }) => {
    await seed(page, { count: 1, palette: ['#e03030'], artworkKind: 2 });
    await page.locator('.track').first().getByRole('button', { name: 'Open player for Café' }).click();
    await expect(full(page).getByRole('img')).toHaveAttribute('alt', 'Source photo for Fixture source');
    await expect.poll(() => tint(page)).not.toBe('');
    let release;
    const requested = new Promise(resolve => { release = resolve; });
    await page.route('**/delayed-player-art.png', route => { release(route); });
    await full(page).getByRole('img').evaluate(img => { img.src = '/delayed-player-art.png'; });
    const pending = await requested;
    await expect.poll(() => tint(page)).toBe('');
    const blue = await page.evaluate(() => {
        const canvas = document.createElement('canvas'); canvas.width = canvas.height = 32;
        const context = canvas.getContext('2d'); context.fillStyle = '#3060e0'; context.fillRect(0, 0, 32, 32);
        return canvas.toDataURL('image/png');
    });
    await pending.fulfill({ contentType: 'image/png', body: Buffer.from(blue.split(',')[1], 'base64') });
    await expect.poll(async () => (await tint(page)).match(/\d+/g)?.map(Number)[2] > 100).toBe(true);
    await full(page).getByRole('img').evaluate(img => {
        globalThis.previousPlayerImage = img;
        const canvas = document.createElement('canvas'); canvas.width = canvas.height = 32;
        const context = canvas.getContext('2d'); context.fillStyle = '#e03030'; context.fillRect(0, 0, 32, 32);
        const replacement = new Image(); replacement.alt = img.alt; replacement.src = canvas.toDataURL('image/png'); img.replaceWith(replacement);
    });
    await expect.poll(async () => (await tint(page)).match(/\d+/g)?.map(Number)[0] > 100).toBe(true);
    const red = await tint(page);
    await page.evaluate(() => { previousPlayerImage.dispatchEvent(new Event('load')); previousPlayerImage.dispatchEvent(new Event('error')); });
    expect(await tint(page)).toBe(red);
    await full(page).getByRole('img').evaluate(img => { img.src = 'data:image/png;base64,broken'; });
    await expect.poll(() => tint(page)).toBe('');
    await full(page).getByRole('button', { name: 'Minimize player' }).click();
    await page.locator('.track').first().getByRole('button', { name: 'Open player for Café' }).click();
    await expect.poll(() => tint(page)).toBe(red);
});

test('rapid preview navigation cannot retain another track color', async ({ page }) => {
    await seed(page, { palette: ['#e03030', '#3060e0', null] });
    await page.locator('.track').first().getByRole('button', { name: 'Open player for Café' }).click();
    await expect.poll(() => tint(page)).not.toBe('');
    await full(page).getByRole('button', { name: 'Next track' }).dblclick();
    await expect(full(page).getByRole('heading', { name: 'Live session', exact: true })).toBeVisible();
    await expect.poll(() => tint(page)).toBe('');
    await full(page).getByRole('button', { name: 'Previous track' }).click();
    await expect.poll(async () => (await tint(page)).match(/\d+/g)?.map(Number)[2] > 100).toBe(true);
});

for (const width of [390, 1440]) {
    test(`mini player keeps a subtle current-song tint across previews and track changes at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 });
        await seed(page, { count: 3, palette: ['#e03030', '#3060e0', null] });
        await start(page);
        const dockTint = () => dock(page).evaluate(el => el.style.getPropertyValue('--player-artwork-color'));
        await expect.poll(dockTint).not.toBe('');
        const red = await dockTint();
        await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
        await expect.poll(() => tint(page)).toBe(red);
        await full(page).getByRole('button', { name: 'Minimize player' }).click();
        await dock(page).getByRole('button', { name: 'Pause playback' }).click();
        await expect.poll(() => dock(page).evaluate(el => el.getAnimations().length)).toBe(0);
        const contrast = await dock(page).evaluate(el => {
            const canvas = document.createElement('canvas'); canvas.width = canvas.height = 1;
            const context = canvas.getContext('2d');
            const rgb = color => { context.clearRect(0, 0, 1, 1); context.fillStyle = color; context.fillRect(0, 0, 1, 1); return [...context.getImageData(0, 0, 1, 1).data].slice(0, 3); };
            const lum = values => values.map(v => { const c = v / 255; return c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4; }).reduce((sum, c, j) => sum + c * [.2126, .7152, .0722][j], 0);
            const background = rgb(getComputedStyle(el).backgroundColor);
            const ratio = color => (lum(rgb(color)) + .05) / (lum(background) + .05);
            return {
                background,
                darker: lum(background) < lum(rgb(el.style.getPropertyValue('--player-artwork-color'))),
                text: ['.music-player-info strong', '.music-player-info > span > span', '.music-player-status'].map(selector => ratio(getComputedStyle(el.querySelector(selector)).color)),
                action: ratio(getComputedStyle(el.querySelector('.music-player-toggle')).backgroundColor),
                seek: ratio(getComputedStyle(el.querySelector('.music-seekbar')).color)
            };
        });
        expect(contrast.darker).toBe(true);
        expect(contrast.background[0]).toBeGreaterThan(contrast.background[1] * 1.5);
        for (const ratio of contrast.text) expect(ratio).toBeGreaterThanOrEqual(4.5);
        expect(contrast.action).toBeGreaterThanOrEqual(3); expect(contrast.seek).toBeGreaterThanOrEqual(3);
        await expect(dock(page)).toHaveCSS('background-image', 'none');
        await expect(dock(page)).toHaveCSS('transition-duration', '0.35s');
        await fs.mkdir('artifacts/player-colors', { recursive: true });
        await page.screenshot({ path: `artifacts/player-colors/mini-red-${width}.png` });
        await page.getByRole('button', { name: 'Open player for Night Drive', exact: true }).click();
        await expect.poll(() => tint(page)).not.toBe(red);
        expect(await dockTint()).toBe(red);
        await full(page).getByRole('button', { name: 'Play song' }).click();
        await expect.poll(async () => (await dockTint()).match(/\d+/g)?.map(Number)[2] > 100).toBe(true);
        await expect.poll(dockTint).toBe(await tint(page));
        await full(page).getByRole('button', { name: 'Minimize player' }).click();
        await dock(page).getByRole('img').evaluate(img => { img.src = 'data:image/png;base64,broken'; });
        await expect.poll(dockTint).toBe('');
        await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
        await full(page).getByRole('button', { name: 'Next track' }).click();
        await expect(dock(page).locator('.music-player-info strong')).toHaveText('Live session');
        await expect.poll(dockTint).toBe('');
        await full(page).getByRole('button', { name: 'Minimize player' }).click();
        await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
        await full(page).getByRole('button', { name: 'Previous track' }).click();
        await expect.poll(async () => (await dockTint()).match(/\d+/g)?.map(Number)[2] > 100).toBe(true);
    });
}

for (const width of [320, 390, 430, 820, 1100, 1440, 1920]) {
    test(`cards, dock and full-screen player fit at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: width <= 430 ? 900 : 1000 });
        await seed(page);
        const first = page.locator('.track').first();
        const play = first.getByRole('button', { name: 'Stream', exact: true });
        const box = await play.boundingBox();
        expect(box.width).toBeGreaterThanOrEqual(44); expect(box.height).toBeGreaterThanOrEqual(44);
        await fs.mkdir('artifacts/player', { recursive: true });
        await page.screenshot({ path: `artifacts/player/library-${width}.png`, fullPage: true });
        await first.getByRole('button', { name: 'Open player for Café', exact: true }).click();
        await expect(full(page)).toBeVisible();
        await expect(full(page).getByRole('status')).toHaveText('Ready to play');
        await expect(full(page).getByRole('slider', { name: 'Playback position' })).toBeDisabled();
        expect(await page.evaluate(() => !!globalThis.fixtureAudio)).toBe(false);
        expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
        const art = await full(page).locator('.music-art').boundingBox();
        expect(Math.abs(art.width - art.height)).toBeLessThan(1);
        const modal = await full(page).boundingBox();
        expect(modal.width).toBe(width); expect(modal.height).toBe(width <= 430 ? 900 : 1000);
        await page.screenshot({ path: `artifacts/player/fullscreen-ready-${width}.png` });
        await full(page).getByRole('button', { name: 'Play song', exact: true }).click();
        await expect(full(page).getByRole('status')).toContainText('Streaming');
        await expect(full(page).locator('.music-seekbar')).toHaveClass(/is-playing/);
        const range = await full(page).getByRole('slider', { name: 'Playback position' }).boundingBox();
        expect(range.height).toBeGreaterThanOrEqual(44);
        await full(page).getByRole('slider', { name: 'Playback position' }).fill('12');
        await full(page).getByRole('slider', { name: 'Playback position' }).dispatchEvent('change');
        await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThanOrEqual(12);
        await page.screenshot({ path: `artifacts/player/fullscreen-playing-${width}.png` });
        await full(page).getByRole('button', { name: 'Pause playback', exact: true }).click();
        await expect(full(page).getByRole('status')).toHaveText('Paused');
        await expect(full(page).locator('.music-seekbar')).not.toHaveClass(/is-playing/);
        await full(page).getByRole('button', { name: 'Minimize player', exact: true }).click();
        await expect(full(page)).toHaveCount(0);
        await expect(first.getByRole('button', { name: 'Open player for Café' })).toBeFocused();
        await expect(dock(page).getByRole('status')).toHaveText('Paused');
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.screenshot({ path: `artifacts/player/dock-${width}.png` });
        await dock(page).getByRole('button', { name: 'Resume playback', exact: true }).click();
        await expect(dock(page).getByRole('status')).toContainText('Streaming');
        await page.screenshot({ path: `artifacts/player/dock-playing-${width}.png` });
    });
}

test('previewing another song preserves audio and its queue until explicit Play', async ({ page }) => {
    await seed(page); await start(page);
    const original = await page.evaluate(() => { globalThis.originalAudio = fixtureAudio; return fixtureAudio.currentTime; });
    await page.getByRole('button', { name: 'Open player for Night Drive', exact: true }).click();
    await expect(full(page).getByRole('status')).toHaveText('Ready to play');
    await expect(full(page)).toContainText('Currently playing: Café');
    await expect(full(page).getByRole('slider', { name: 'Playback position' })).toHaveValue('0');
    await full(page).getByRole('button', { name: 'Next track' }).click();
    await expect(full(page).getByRole('heading', { name: 'Live session', exact: true })).toBeVisible();
    await expect.poll(() => page.evaluate(() => originalAudio.currentTime)).toBeGreaterThan(original);
    expect(await page.evaluate(() => fixtureAudio === originalAudio && !fixtureAudio.paused)).toBe(true);
    await full(page).getByRole('button', { name: 'Play song' }).click();
    await expect(full(page).getByRole('status')).toContainText('Streaming');
    await full(page).getByRole('button', { name: 'Minimize player' }).click();
    await expect(dock(page).locator('.music-player-info strong')).toHaveText('Live session');
    await page.locator('.track').nth(2).getByRole('button', { name: 'Pause', exact: true }).click();
    await expect(dock(page).getByRole('status')).toHaveText('Paused');
    await expect(full(page)).toHaveCount(0);
});

test('seeking previews locally, commits once, and synchronizes both player views', async ({ page }) => {
    await seed(page); await start(page);
    await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
    await full(page).getByRole('button', { name: 'Pause playback' }).click();
    const seek = full(page).getByRole('slider', { name: 'Playback position' });
    const before = await page.evaluate(() => fixtureAudio.currentTime);
    await seek.evaluate(el => { el.value = '12'; el.dispatchEvent(new Event('input', { bubbles: true })); });
    await expect(seek).toHaveAttribute('aria-valuetext', '0:12 of 0:30');
    await expect.poll(() => seek.inputValue()).toBe('12');
    expect(await page.evaluate(() => fixtureAudio.currentTime)).toBeCloseTo(before, 0);
    await seek.dispatchEvent('change');
    await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeCloseTo(12, 0);
    await full(page).getByRole('button', { name: 'Minimize player' }).click();
    await expect(dock(page).getByRole('slider', { name: 'Playback position' })).toHaveValue('12');
    await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
    await expect(full(page).getByRole('slider', { name: 'Playback position' })).toHaveValue('12');
    await full(page).getByRole('slider', { name: 'Volume', exact: true }).fill('0.4');
    await expect.poll(() => page.evaluate(() => fixtureAudio.volume)).toBeCloseTo(.4, 1);
});

test('nested Back dismissal restores focus and does not stop playback', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seed(page); await start(page);
    await dock(page).getByRole('button', { name: 'Expand player', exact: true }).click();
    await full(page).getByRole('button', { name: 'More actions', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Song actions', exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.channelwaveDismissDialog())).toBe(true);
    await expect(page.getByRole('dialog', { name: 'Song actions', exact: true })).not.toBeVisible();
    await expect(full(page).getByRole('button', { name: 'More actions' })).toBeFocused();
    expect(await page.evaluate(() => window.channelwaveDismissDialog())).toBe(true);
    await expect(full(page)).toHaveCount(0);
    await expect(dock(page).getByRole('button', { name: 'Expand player' })).toBeFocused();
    expect(await page.evaluate(() => !fixtureAudio.paused)).toBe(true);
    expect(await page.evaluate(() => window.channelwaveDismissDialog())).toBe(false);
});

test('protected previews expose restrictions and never prepare audio', async ({ page }) => {
    await seed(page, { protectedTrack: true });
    await page.getByRole('button', { name: 'Open player for Café' }).click();
    await expect(full(page).getByRole('button', { name: 'Play song' })).toBeDisabled();
    await expect(full(page)).toContainText('This source restricts saving');
    await full(page).getByRole('button', { name: 'More actions' }).click();
    const actions = page.getByRole('dialog', { name: 'Song actions' });
    await expect(actions.getByRole('button', { name: 'Save offline' })).toBeDisabled();
    await expect(actions.getByRole('button', { name: 'Download', exact: true })).toBeDisabled();
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
});

test('player actions persist favorites and list membership, then edit with clean focus', async ({ page }) => {
    await seed(page);
    await page.getByRole('button', { name: 'Open player for Café' }).click();
    await full(page).getByRole('button', { name: 'Favorite track', exact: true }).click();
    await expect(full(page).getByRole('button', { name: 'Remove favorite', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await clickTrackAction(full(page), 'Add to playlist');
    await expect(full(page).getByRole('status').filter({ hasText: 'Added Café' })).toBeVisible();
    await clickTrackAction(full(page), 'Edit details');
    await expect(full(page)).toHaveCount(0);
    await expect(page.getByRole('dialog', { name: 'Edit details', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: 'Open player for Café' })).toBeFocused();
    await page.reload();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openLibrary(page);
    await page.getByRole('button', { name: 'Open player for Café' }).click();
    await expect(full(page).getByRole('button', { name: 'Remove favorite', exact: true })).toHaveAttribute('aria-pressed', 'true');
});

test('navigation dismisses player and nested actions without interrupting audio', async ({ page }) => {
    await seed(page); await start(page);
    await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
    await full(page).getByRole('button', { name: 'More actions' }).click();
    await page.evaluate(() => { location.hash = '#view=account'; });
    await expect(page.getByRole('heading', { name: 'Profile', exact: true })).toBeVisible();
    await expect(page.locator('dialog[open]')).toHaveCount(0);
    await expect(page.locator('html')).not.toHaveClass(/modal-open/);
    expect(await page.evaluate(() => !fixtureAudio.paused)).toBe(true);
});

test('short landscape, enlarged text and reduced motion keep the player operable', async ({ page }) => {
    await page.setViewportSize({ width: 844, height: 390 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await seed(page); await start(page);
    await dock(page).getByRole('button', { name: 'Open full-screen player' }).click();
    await expect(full(page).locator('.music-seek-wave rect')).toHaveCSS('animation-name', 'none');
    await expect(full(page)).toHaveCSS('transition-duration', '0s');
    await expect(dock(page)).toHaveCSS('transition-duration', '0s');
    await full(page).evaluate(el => { el.querySelectorAll('h3, p, strong, small').forEach(x => x.style.fontSize = `${parseFloat(getComputedStyle(x).fontSize) * 2}px`); });
    await full(page).getByRole('button', { name: 'Pause playback' }).click();
    expect(await full(page).evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await full(page).getByRole('button', { name: 'Minimize player' }).click();
    await expect(dock(page).getByRole('status')).toHaveText('Paused');
});

test('full-screen saved audio works after an offline reload without Telegram', async ({ page, context }) => {
    await seed(page);
    await clickTrackAction(page.locator('.track').first(), 'Save offline');
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-feedback')).toContainText('Saved Café offline');
    await context.setOffline(true); await page.reload();
    await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Open player for Café', exact: true }).click();
    await expect(full(page).getByRole('status')).toHaveText('Ready to play');
    await expect.poll(() => full(page).evaluate(el => el.style.getPropertyValue('--player-artwork-color'))).not.toBe('');
    await full(page).getByRole('button', { name: 'Play song', exact: true }).click();
    await expect(full(page).getByRole('status')).toHaveText('Playing offline');
    await expect.poll(() => dock(page).evaluate(el => el.style.getPropertyValue('--player-artwork-color'))).toBe(await tint(page));
    await expect.poll(() => page.evaluate(() => fixtureAudio.currentTime)).toBeGreaterThan(0);
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
});

test('canceling full-screen preparation rejects late audio without autoplay', async ({ page }) => {
    await seed(page);
    await page.evaluate(() => { globalThis.fixtureHoldFirstStream = true; });
    await page.getByRole('button', { name: 'Open player for Café', exact: true }).click();
    await full(page).getByRole('button', { name: 'Play song', exact: true }).click();
    await expect.poll(() => page.evaluate(() => typeof releaseFixtureFirstStream)).toBe('function');
    await full(page).getByRole('button', { name: 'Cancel stream', exact: true }).click();
    await expect(full(page)).toHaveCount(0);
    await expect(dock(page)).toHaveCount(0);
    await page.evaluate(() => { fixtureHoldFirstStream = false; releaseFixtureFirstStream(); });
    await expect(page.locator('.track').first().getByRole('button', { name: 'Stream', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => !globalThis.fixtureAudio || fixtureAudio.paused)).toBe(true);
});

test('full-screen preparation failure exposes a working retry', async ({ page }) => {
    await seed(page);
    await page.evaluate(() => { globalThis.fixtureStreamFailure = true; });
    await page.getByRole('button', { name: 'Open player for Café', exact: true }).click();
    await full(page).getByRole('button', { name: 'Play song', exact: true }).click();
    await expect(full(page).getByRole('alert')).toHaveCount(1);
    await expect(full(page).getByRole('alert')).toContainText('Synthetic stream transfer failure');
    await full(page).getByRole('button', { name: 'Retry playback', exact: true }).click();
    await expect(full(page).getByRole('status')).toContainText('Streaming from Telegram');
    await full(page).getByRole('button', { name: 'Minimize player', exact: true }).click();
    await expect(page.locator('.track').first().getByRole('status')).toHaveText('Now playing');
});

test('Next retains the playing queue context after navigating to Offline', async ({ page }) => {
    await seed(page);
    await clickTrackAction(page.locator('.track').first(), 'Save offline');
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-feedback')).toContainText('Saved Café offline');
    await page.locator('.track').first().getByRole('button', { name: 'Play', exact: true }).click();
    await expect(dock(page).getByRole('status')).toHaveText('Playing offline');
    await page.getByRole('group', { name: 'Filter music' }).getByRole('button', { name: 'Offline', exact: true }).click();
    await expect(page.locator('.track')).toHaveCount(1);
    await dock(page).getByRole('button', { name: 'Open full-screen player', exact: true }).click();
    await full(page).getByRole('button', { name: 'Next track', exact: true }).click();
    await expect(full(page).getByRole('heading', { name: 'Night Drive', exact: true })).toBeVisible();
    await expect(full(page).getByRole('status')).toHaveText('Streaming from Telegram');
    await expect(full(page).getByRole('alert')).toHaveCount(0);
});
