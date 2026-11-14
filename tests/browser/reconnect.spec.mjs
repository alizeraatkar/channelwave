import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture, openLibrary, openCategory, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';
import { installLoadingGates } from './fixtures/loading.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const configuration = { apiId: 1, apiHash: '0'.repeat(32) };

// The fixture client does not persist TDLib files. Emulate the pinned tdweb
// filesystem's existence using an empty synthetic database, never a real session.
async function saveSyntheticSession(page) {
    await page.evaluate(() => new Promise((resolve, reject) => {
        const request = indexedDB.open('/channelwave-telegram/dbfs', 1);
        request.onsuccess = () => { request.result.close(); resolve(); };
        request.onerror = () => reject(request.error);
    }));
}

async function connected(page) {
    await expect(page.locator('.sidebar-bottom')).toHaveText('Connected to Telegram', { timeout: 15000 });
}

test('a fresh profile waits for Connect and does not create a session while checking', async ({ page }) => {
    await installTelegramFixture(page);
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled({ timeout: 15000 });
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    expect(await page.evaluate(async p => (await import(p)).hasSavedTelegramSession(), modulePath)).toBe(false);
    expect(await page.evaluate(async () => (await indexedDB.databases()).some(db => db.name === '/channelwave-telegram/dbfs'))).toBe(false);
    await connectFixture(page);
    await connected(page);
});

test('a disconnected library URL waits for the saved-session probe before choosing Offline', async ({ page }) => {
    await installTelegramFixture(page);
    await installLoadingGates(page, [{ name: 'hasSavedTelegramSession', query: null, stage: 'before' }]);
    await page.goto('/#view=tracks');
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('hasSavedTelegramSession')), { timeout: 15000 }).toBe(1);
    await expect(page.locator('.app-shell')).toBeVisible();
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    await page.evaluate(() => fixtureReleaseOperation('hasSavedTelegramSession'));
    await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
});

test('a saved session reconnects once on reload and keeps the requested library view', async ({ page }) => {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(1);
    await saveSyntheticSession(page);
    await page.reload();
    await connected(page);
    await expect(page.getByRole('heading', { name: 'Your music', exact: true })).toBeVisible();
    await expect(page.locator('.track')).toHaveCount(1);
    expect(await page.evaluate(() => fixtureCalls.filter(call => call === 'getAuthorizationState').length)).toBe(1);
    expect(await page.evaluate(() => fixtureCalls.includes('downloadFile'))).toBe(false);
    await openRoot(page, 'Profile');
    await expect(page.getByRole('button', { name: 'Sign out and erase local data', exact: true })).toBeVisible();
});

test('saved offline audio remains usable during automatic reconnection', async ({ page }) => {
    await installTelegramFixture(page, 'progressive');
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page);
    await clickTrackAction(page.locator('.track'), 'Save offline');
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-feedback')).toContainText('Saved');
    await saveSyntheticSession(page);
    let release;
    const requested = new Promise(resolve => { release = resolve; });
    await page.route('**/channelwave.local.json', route => { release(route); });
    await page.reload();
    const pending = await requested;
    await expect(page.locator('.sidebar-bottom')).toHaveText('Telegram not connected');
    await openCategory(page, 'Offline');
    await page.locator('.track').getByRole('button', { name: 'Play', exact: true }).click();
    await expect(page.locator('.music-player-status')).toHaveText('Playing offline', { timeout: 15000 });
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    await pending.fulfill({ json: configuration });
    await connected(page);
    await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
    await expect(page.locator('.music-player-status')).toHaveText('Playing offline');
    expect(await page.evaluate(() => fixtureCalls.filter(call => call === 'getAuthorizationState').length)).toBe(1);
});

test('automatic connection failure stays recoverable through manual Connect', async ({ page }) => {
    await installTelegramFixture(page);
    await installLoadingGates(page);
    await page.goto('/');
    await expect(page.locator('.app-shell')).toBeVisible({ timeout: 20000 });
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    await expect.poll(() => page.evaluate(() => fixtureSessionProbes), { timeout: 15000 }).toBe(1);
    await saveSyntheticSession(page);
    let attempts = 0;
    await page.route('**/channelwave.local.json', route => { attempts++; return route.fulfill({ status: 503 }); });
    await page.reload();
    await expect(page.getByRole('alert')).toContainText('configuration is missing');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    expect(attempts).toBe(1);
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    await page.route('**/channelwave.local.json', route => route.fulfill({ json: configuration }));
    await connectFixture(page);
    await connected(page);
    await expect(page.getByRole('alert')).toHaveCount(0);
});

test('an expired saved session opens the normal authentication form', async ({ page }) => {
    await installTelegramFixture(page, 'auth');
    await installLoadingGates(page);
    await page.goto('/');
    await expect(page.locator('.app-shell')).toBeVisible({ timeout: 20000 });
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    await expect.poll(() => page.evaluate(() => fixtureSessionProbes), { timeout: 15000 }).toBe(1);
    await saveSyntheticSession(page);
    await page.reload();
    await expect(page.locator('#auth-value')).toBeVisible();
    await expect(page.locator('#auth-value')).toHaveAttribute('type', 'tel');
    await expect(page.getByRole('button', { name: 'Continue', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls.includes('setAuthenticationPhoneNumber'))).toBe(false);
    expect(await page.evaluate(() => fixtureCalls.includes('getMe'))).toBe(false);
});

test('pending sign-out cleanup prevents automatic session restoration', async ({ page }) => {
    await installTelegramFixture(page);
    await installLoadingGates(page);
    await page.goto('/');
    await expect(page.locator('.app-shell')).toBeVisible({ timeout: 20000 });
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    await expect.poll(() => page.evaluate(() => fixtureSessionProbes), { timeout: 15000 }).toBe(1);
    await saveSyntheticSession(page);
    await page.evaluate(async p => {
        await (await import(p)).saveState(0, 'pending-signout-v1', JSON.stringify({ Account: 700, RemoteLogout: true }));
    }, modulePath);
    await page.reload();
    await expect(page.getByRole('button', { name: 'Retry sign-out cleanup', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
});

test('sign-out erases saved-session detection and does not reconnect after reload', async ({ page }) => {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await connected(page);
    await saveSyntheticSession(page);
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely and erased local account data' })).toBeVisible();
    expect(await page.evaluate(async p => (await import(p)).hasSavedTelegramSession(), modulePath)).toBe(false);
    await page.reload();
    await page.getByRole('button', { name: 'ChannelWave home', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled();
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
});
