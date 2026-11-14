import { test, expect } from '@playwright/test';
import { installTelegramFixture, connectFixture, addFixtureSource, openLibrary, openRoot, clickTrackAction } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const checkpointKey = 'pending-signout-v1';

async function setup(page) {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page);
    await clickTrackAction(page, 'Save offline');
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1);
}

async function checkpoint(page) {
    return page.evaluate(async ({ modulePath, checkpointKey }) => JSON.parse(await (await import(modulePath)).readState(0, checkpointKey)), { modulePath, checkpointKey });
}

for (const stage of ['preparation', 'logout']) {
    test(`interrupted ${stage} preserves sign-out intent and recovers without restoring the account`, async ({ page }) => {
        await installTelegramFixture(page);
        await page.route('**/_content/ChannelWave.Platform.Browser/channelwave.js', async route => {
            const response = await route.fetch();
            const body = (await response.text()).replace('export function clearAudio(', 'function securityOriginal_clearAudio(')
                + `\nexport async function clearAudio(...args) {
                    if (globalThis.securityHoldPreparation) {
                        globalThis.securityPreparationEntered = true;
                        await new Promise(() => {});
                    }
                    return securityOriginal_clearAudio(...args);
                }\n`;
            await route.fulfill({ response, body });
        });
        await page.addInitScript(() => {
            const send = tdweb.default.prototype.send;
            tdweb.default.prototype.send = async function (query) {
                if (query['@type'] === 'logOut' && globalThis.securityHoldLogout) {
                    globalThis.securityLogoutEntered = true;
                    await new Promise(() => {});
                }
                if (query['@type'] === 'logOut' && sessionStorage.getItem('securityHoldRecoveredClosure')) {
                    fixtureCalls.push('logOut');
                    globalThis.completeSecurityLogout = () => this.options.onUpdate({
                        '@type': 'updateAuthorizationState', authorization_state: { '@type': 'authorizationStateClosed' }
                    });
                    return { '@type': 'ok' };
                }
                return send.call(this, query);
            };
            const remove = IDBFactory.prototype.deleteDatabase;
            globalThis.securitySessionDeletions = 0;
            IDBFactory.prototype.deleteDatabase = function (name) {
                if (name === 'channelwave-telegram' || name === '/channelwave-telegram/dbfs') ++securitySessionDeletions;
                return remove.call(this, name);
            };
        });
        await page.goto('/');
        await connectFixture(page);
        await addFixtureSource(page);
        await openLibrary(page);
        await clickTrackAction(page, 'Save offline');
        await page.evaluate(() => completeFixtureDownload());
        await expect(page.locator('.music-saved')).toHaveCount(1);
        await openRoot(page, 'Profile');
        await page.evaluate(async stage => {
            // Represent a saved TDLib profile so recovery must close it before deletion.
            for (const name of ['channelwave-telegram', '/channelwave-telegram/dbfs']) await new Promise((resolve, reject) => {
                const request = indexedDB.open(name);
                request.onsuccess = () => { request.result.close(); resolve(); };
                request.onerror = () => reject(request.error);
            });
            globalThis.securityHoldPreparation = stage === 'preparation';
            globalThis.securityHoldLogout = stage === 'logout';
        }, stage);
        await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
        await expect.poll(() => page.evaluate(stage => stage === 'preparation' ? !!globalThis.securityPreparationEntered : !!globalThis.securityLogoutEntered, stage)).toBe(true);
        expect(await checkpoint(page)).toEqual({ Account: 700, RemoteLogout: false, Phase: 'requested' });

        await page.evaluate(() => sessionStorage.setItem('securityHoldRecoveredClosure', '1'));
        await page.reload();
        await expect(page.getByRole('button', { name: 'Retry sign-out cleanup', exact: true })).toBeEnabled();
        expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
        await expect(page.locator('.track')).toHaveCount(0);
        await expect(page.getByRole('region', { name: 'Now playing', exact: true })).toHaveCount(0);
        await page.getByRole('button', { name: 'Retry sign-out cleanup', exact: true }).click();
        await expect.poll(() => page.evaluate(() => typeof globalThis.completeSecurityLogout)).toBe('function');
        expect(await page.evaluate(() => securitySessionDeletions)).toBe(0);
        const calls = await page.evaluate(() => fixtureCalls);
        expect(calls).toContain('getAuthorizationState');
        expect(calls).toContain('logOut');
        for (const method of ['getMe', 'getChats', 'loadChats', 'searchChatMessages', 'downloadFile']) expect(calls).not.toContain(method);
        expect((await checkpoint(page)).Phase).toBe('requested');
        await page.evaluate(() => completeSecurityLogout());
        await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely and erased local account data' })).toBeVisible();
        expect(await checkpoint(page)).toBeNull();
        expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
        expect(await page.evaluate(() => securitySessionDeletions)).toBe(2);
    });
}

test('requested checkpoint can finish erasure when a previous attempt already removed the session', async ({ page }) => {
    await setup(page);
    await page.evaluate(async ({ modulePath, checkpointKey }) => {
        await (await import(modulePath)).saveState(0, checkpointKey, JSON.stringify({ Account: 700, RemoteLogout: false, Phase: 'requested' }));
    }, { modulePath, checkpointKey });
    await page.reload();
    await page.getByRole('button', { name: 'Retry sign-out cleanup', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Local account data erased. Remote logout was not confirmed.' })).toBeVisible();
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
    expect(await checkpoint(page)).toBeNull();
    expect(await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700)), modulePath)).toEqual([]);
});
