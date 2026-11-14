import { openRoot, openCategory } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture, openLibrary, openChannels, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';

test.beforeEach(async ({ page }) => {
    page.auditErrors = [];
    page.on('pageerror', error => page.auditErrors.push(error.message));
});

test.afterEach(async ({ page }) => {
    expect(page.auditErrors).toEqual([]);
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('checkpoint storage failure does not prevent sign-out and erasure', async ({ page }) => {
    await setup(page);
    await page.evaluate(() => {
        const transaction = IDBDatabase.prototype.transaction;
        IDBDatabase.prototype.transaction = function (stores, mode, ...args) {
            if (stores === 'state' && mode === 'readwrite') throw new DOMException('Synthetic full checkpoint storage', 'QuotaExceededError');
            return transaction.call(this, stores, mode, ...args);
        };
    });
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely and erased local account data' })).toBeVisible();
    expect(await page.evaluate(() => fixtureCalls.includes('logOut'))).toBe(true);
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).readSources(700)), modulePath)).toEqual([]);
    await expect(page.getByRole('alert')).toHaveCount(0);
});

async function setup(page) {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(1);
}

test('account lookup retries transient failures and offers recovery after exhausting retries', async ({ page }) => {
    await installTelegramFixture(page);
    await page.addInitScript(() => {
        const send = tdweb.default.prototype.send;
        globalThis.auditAccountAttempts = 0;
        globalThis.auditAccountUnavailable = true;
        tdweb.default.prototype.send = async function (query) {
            if (query['@type'] === 'getMe') {
                ++auditAccountAttempts;
                if (auditAccountUnavailable) throw { code: 500, message: 'Synthetic account lookup failure' };
            }
            return send.call(this, query);
        };
    });
    await page.goto('/');
    await expect(async () => {
        const connect = page.getByRole('button', { name: 'Connect account', exact: true });
        if (await connect.isVisible() && await connect.isEnabled()) await connect.click();
        expect(await page.evaluate(() => globalThis.auditAccountAttempts)).toBeGreaterThan(0);
    }).toPass({ timeout: 20000 });
    const retry = page.getByRole('button', { name: 'Retry account connection', exact: true });
    await expect(retry).toBeEnabled({ timeout: 15000 });
    expect(await page.evaluate(() => auditAccountAttempts)).toBe(3);
    await page.evaluate(() => { auditAccountUnavailable = false; });
    await retry.click();
    await expect(page.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeVisible();
    await openRoot(page, 'Profile');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely' })).toBeVisible();
});

for (const action of ['cancel', 'sign out', 'remove source']) {
    test(`final export read cannot download after ${action}`, async ({ page }) => {
        await setup(page);
        const exported = [];
        page.on('download', file => exported.push(file.suggestedFilename()));
        await page.evaluate(() => {
            const send = tdweb.default.prototype.send;
            tdweb.default.prototype.send = async function (query) {
                const response = await send.call(this, query);
                if (query['@type'] === 'readFile') await new Promise(resolve => { globalThis.releaseAuditRead = resolve; });
                return response;
            };
            const click = HTMLAnchorElement.prototype.click;
            globalThis.auditExportClicks = 0;
            HTMLAnchorElement.prototype.click = function () { if (this.download) ++auditExportClicks; return click.call(this); };
        });
        await clickTrackAction(page, 'Download');
        await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
        await page.evaluate(() => completeFixtureDownload());
        await expect.poll(() => page.evaluate(() => typeof releaseAuditRead)).toBe('function');
        if (action === 'cancel') {
            await page.getByRole('button', { name: 'Cancel download', exact: true }).click();
            await expect(page.locator('.music-feedback')).toContainText('Download canceled');
        } else if (action === 'sign out') {
            await openRoot(page, 'Profile');
            await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
            await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely' })).toBeVisible();
        } else {
            await openChannels(page);
            await page.getByRole('button', { name: 'Remove from library: :)', exact: true }).click();
            await expect(page.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeVisible();
        }
        await page.evaluate(async path => {
            releaseAuditRead();
            // Drain the same client response path before inspecting export side effects.
            await (await import(path)).sendTelegram(JSON.stringify({ '@type': 'getOption', name: 'version' }));
        }, modulePath).catch(error => { if (action !== 'sign out') throw error; });
        expect(await page.evaluate(() => auditExportClicks)).toBe(0);
        expect(exported).toEqual([]);
        await expect(page.locator('.music-feedback').filter({ hasText: 'Download sent' })).toHaveCount(0);
    });
}

for (const stage of ['library', 'session']) for (const reload of [false, true]) {
    test(`sign-out retries failed ${stage} cleanup ${reload ? 'after reload' : 'in the open app'} and completes independent stages`, async ({ page }) => {
        await setup(page);
        await clickTrackAction(page, 'Save offline');
        await page.evaluate(() => completeFixtureDownload());
        await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
        await page.evaluate(stage => {
            globalThis.auditCleanupBlocked = true;
            globalThis.auditSessionDeletionAttempts = 0;
            const transaction = IDBDatabase.prototype.transaction;
            IDBDatabase.prototype.transaction = function (stores, mode, ...args) {
                const tx = transaction.call(this, stores, mode, ...args);
                if (stage === 'library' && auditCleanupBlocked && mode === 'readwrite' &&
                    Array.from(stores).includes('state') && Array.from(stores).includes('offlineAudio')) queueMicrotask(() => tx.abort());
                return tx;
            };
            const remove = IDBFactory.prototype.deleteDatabase;
            IDBFactory.prototype.deleteDatabase = function (name) {
                if (name === 'channelwave-telegram') {
                    ++auditSessionDeletionAttempts;
                    if (stage === 'session' && auditCleanupBlocked) throw new DOMException('Synthetic session cleanup failure', 'UnknownError');
                }
                return remove.call(this, name);
            };
        }, stage);
        await openRoot(page, 'Profile');
        await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
        await expect(page.getByRole('button', { name: 'Retry sign-out cleanup', exact: true })).toBeEnabled();
        await expect(page.getByRole('alert')).toContainText('cleanup');
        expect(await page.evaluate(() => auditSessionDeletionAttempts)).toBe(1);
        await expect(page.getByRole('status').filter({ hasText: 'erased local account data' })).toHaveCount(0);
        if (reload)
        {
            await page.reload();
            await page.getByText('Connection details', { exact: true }).click();
            await page.getByRole('button', { name: 'Check Telegram engine', exact: true }).click();
            await expect(page.getByRole('alert')).toContainText('Finish sign-out cleanup');
            expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
        }
        else await page.evaluate(() => { auditCleanupBlocked = false; });
        await page.getByRole('button', { name: 'Retry sign-out cleanup', exact: true }).click();
        await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely and erased local account data' })).toBeVisible();
        await expect(page.getByRole('alert')).toHaveCount(0);
        expect(await page.evaluate(async path => {
            const lib = await import(path);
            return { sources: JSON.parse(await lib.readSources(700)), offline: JSON.parse(await lib.offlineTracks(700)) };
        }, modulePath)).toEqual({ sources: [], offline: [] });
        expect(await page.evaluate(async path => await (await import(path)).readState(0, 'pending-signout-v1'), modulePath)).toBe('null');
        if (reload)
        {
            // A recovered app may connect again; completed stages must not skip a later logout.
            await page.getByRole('button', { name: 'Connect account', exact: true }).click();
            await expect(page.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeVisible();
            await openRoot(page, 'Profile');
            await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
            await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely and erased local account data' })).toBeVisible();
            expect(await page.evaluate(() => fixtureCalls.filter(call => call === 'logOut').length)).toBe(1);
        }
    });
}

test('saving offline preserves favorite edits made while the download is pending', async ({ page }) => {
    await setup(page);
    await clickTrackAction(page, 'Save offline');
    await expect.poll(() => page.evaluate(() => typeof completeFixtureDownload)).toBe('function');
    await page.getByRole('button', { name: 'Favorite track', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Remove favorite', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700))[0].favorite, modulePath)).toBe(true);
    await page.getByRole('group', { name: 'Filter music' }).getByRole('button', { name: 'Offline', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Remove favorite', exact: true })).toHaveAttribute('aria-pressed', 'true');
});

test('unreadable cleanup checkpoint blocks offline access until verification recovers', async ({ page }) => {
    await setup(page);
    await clickTrackAction(page, 'Save offline');
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1, { timeout: 30000 });
    await page.evaluate(async path => {
        await (await import(path)).saveState(0, 'pending-signout-v1', JSON.stringify({ Account: 700, RemoteLogout: true }));
    }, modulePath);
    await page.addInitScript(() => {
        globalThis.auditMarkerReadBlocked = true;
        const get = IDBObjectStore.prototype.get;
        IDBObjectStore.prototype.get = function (key) {
            if (this.name === 'state' && Array.isArray(key) && key[0] === 0 && key[1] === 'pending-signout-v1' && auditMarkerReadBlocked)
                throw new DOMException('Synthetic cleanup marker read failure', 'UnknownError');
            return get.call(this, key);
        };
    });
    await page.reload();
    await expect(page.getByRole('alert')).toContainText('Synthetic cleanup marker read failure');
    await openCategory(page, 'Offline');
    await expect(page.getByRole('button', { name: 'Retry local cleanup check', exact: true })).toBeVisible();
    await expect(page.locator('.track')).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    await page.evaluate(() => { auditMarkerReadBlocked = false; });
    await page.getByRole('button', { name: 'Retry local cleanup check', exact: true }).click();
    await page.getByRole('button', { name: 'Retry sign-out cleanup', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely and erased local account data' })).toBeVisible();
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
});
