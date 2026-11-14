import { test, expect } from '@playwright/test';
import { installTelegramFixture, connectFixture, openRoot, openChannels, setConnectionState, observeActivityStatus } from './fixtures/telegram.mjs';
import { installLoadingGates } from './fixtures/loading.mjs';
import { seed } from './fixtures/redesign.mjs';

const activity = page => page.locator('.telegram-activity');
const bridge = '/_content/ChannelWave.Platform.Browser/channelwave.js';

async function connected(page) {
    await installTelegramFixture(page);
    await page.goto('/');
    await connectFixture(page);
    await setConnectionState(page, 'Ready');
    await expect(activity(page)).toBeEmpty();
}

test('brief connection and updating bursts stay silent, including repeated two-second cycles', async ({ page }) => {
    await connected(page);
    await observeActivityStatus(page);
    for (const state of ['Updating', 'Connecting', 'Updating']) {
        await setConnectionState(page, state);
        await page.waitForTimeout(200);
        await expect(activity(page)).toBeEmpty();
        await setConnectionState(page, 'Ready');
        await page.waitForTimeout(1800);
    }
    expect(await page.evaluate(() => fixtureActivityChanges.filter(change => change.text))).toEqual([]);
});

test('sustained activity is delayed, changes continuously, and clears after stable readiness', async ({ page }) => {
    await connected(page);
    await observeActivityStatus(page);
    const started = await page.evaluate(() => performance.now());
    await setConnectionState(page, 'Connecting');
    await page.waitForTimeout(350);
    await expect(activity(page)).toBeEmpty();
    await expect(activity(page)).toHaveText('Connecting…');
    const first = await page.evaluate(() => fixtureActivityChanges.find(change => change.text));
    expect(first.at - started).toBeGreaterThanOrEqual(950);
    for (const [state, label] of [['WaitingForNetwork', 'Waiting for network…'], ['ConnectingToProxy', 'Connecting to proxy…'], ['Updating', 'Updating…']]) {
        await setConnectionState(page, state);
        await expect(activity(page)).toHaveText(label, { timeout: 750 });
    }
    await setConnectionState(page, 'Ready');
    await page.waitForTimeout(150);
    await expect(activity(page)).toHaveText('Updating…');
    await setConnectionState(page, 'Connecting');
    await expect(activity(page)).toHaveText('Connecting…', { timeout: 750 });
    expect(await page.evaluate(() => fixtureActivityChanges.filter(change => !change.text))).toEqual([]);
    await setConnectionState(page, 'Ready');
    await expect(activity(page)).toBeEmpty();
    await expect(activity(page)).toHaveAttribute('aria-live', 'polite');
    await expect(activity(page).locator('[role=status]')).toHaveCount(0);
});

test('shared status survives navigation and pending activity is cancelled by sign-out', async ({ page }) => {
    await connected(page);
    await setConnectionState(page, 'Updating');
    await expect(activity(page)).toHaveText('Updating…');
    for (const root of ['Home', 'Library', 'Settings', 'Profile']) {
        await openRoot(page, root);
        await expect(activity(page)).toHaveText('Updating…');
    }
    await openRoot(page, 'Library');
    await page.getByRole('button', { name: 'Open search', exact: true }).click();
    await expect(activity(page)).toHaveText('Updating…');
    await openRoot(page, 'Profile');
    await setConnectionState(page, 'Ready');
    await expect(activity(page)).toBeEmpty();
    await observeActivityStatus(page);
    await setConnectionState(page, 'Connecting');
    await page.getByRole('button', { name: 'Sign out and erase local data', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Signed out remotely and erased local account data' })).toBeVisible();
    await page.waitForTimeout(1200);
    await expect(activity(page)).toBeEmpty();
    expect(await page.evaluate(() => fixtureActivityChanges.filter(change => change.text))).toEqual([]);
});

test('account changes cancel the previous account’s pending indicator', async ({ page }) => {
    await connected(page);
    await setConnectionState(page, 'Updating');
    await page.waitForTimeout(200);
    await page.evaluate(() => {
        fixtureClient.authorize({ '@type': 'authorizationStateWaitPhoneNumber' });
        fixtureClient.options.onUpdate({ '@type': 'updateConnectionState', state: { '@type': 'connectionStateReady' } });
        globalThis.fixtureAccountId = 701;
        fixtureClient.authorize({ '@type': 'authorizationStateReady' });
    });
    await page.waitForTimeout(1200);
    await expect(activity(page)).toBeEmpty();
    await setConnectionState(page, 'Connecting');
    await expect(activity(page)).toHaveText('Connecting…');
    await page.evaluate(() => fixtureClient.authorize({ '@type': 'authorizationStateWaitPhoneNumber' }));
    await expect(activity(page)).toBeEmpty({ timeout: 750 });
});

test('sustained music catch-up is shown but held routine audits stay silent', async ({ page }) => {
    await installTelegramFixture(page);
    await installLoadingGates(page, [{ name: 'telegram:searchChatMessages', query: null, stage: 'before' },
        { name: 'telegram:searchChatMessages', query: null, stage: 'before' }]);
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Connect account', exact: true })).toBeEnabled({ timeout: 20000 });
    await page.evaluate(async path => {
        const lib = await import(path);
        await lib.savePage(JSON.stringify({ accountId: 700, chatId: -100123, name: 'Fixture source', historyComplete: true }), '[]');
    }, bridge);
    await connectFixture(page);
    await openChannels(page);
    await page.getByRole('button', { name: 'Sync selected sources', exact: true }).click();
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('telegram:searchChatMessages'))).toBe(1);
    await expect(activity(page)).toHaveText('Updating…');
    await page.evaluate(() => fixtureReleaseOperation('telegram:searchChatMessages'));
    const syncProgress = page.getByRole('region', { name: 'Audio from selected sources', exact: true }).getByRole('status');
    await expect(syncProgress).toContainText('Checking older music');
    await expect.poll(() => page.evaluate(() => fixtureOperationPending('telegram:searchChatMessages'))).toBe(1);
    await expect(activity(page)).toBeEmpty();
    await observeActivityStatus(page);
    await page.waitForTimeout(1300);
    expect(await page.evaluate(() => fixtureActivityChanges.filter(change => change.text))).toEqual([]);
    await page.evaluate(() => fixtureReleaseOperation('telegram:searchChatMessages'));
    await expect(syncProgress).toContainText('Up to date');
});

test('a single Home header keeps status, Downloads and tab editing accessible at every width', async ({ page }, info) => {
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.message));
    await seed(page);
    await openRoot(page, 'Home');
    const topbar = page.locator('.app-topbar');
    const tabStrip = page.locator('.music-home > .home-tab-strip:first-child');
    const edit = topbar.getByRole('button', { name: 'Edit Home tabs', exact: true });
    const downloads = topbar.getByRole('button', { name: 'Open Downloads', exact: true });
    await expect(page).toHaveTitle('ChannelWave · Your music');
    await expect(page).toHaveURL(/#view=home(?:&|$)/);
    await expect(page.locator('.music-home .home-track').first()).toBeVisible();
    await expect(page.getByRole('main').getByRole('heading', { name: 'ChannelWave', exact: true })).toHaveCount(0);
    await expect(page.locator('#blazor-error-ui')).toBeHidden();
    for (const width of [1440, 820, 390, 320]) {
        await page.setViewportSize({ width, height: 900 });
        await setConnectionState(page, 'Ready');
        await expect(activity(page)).toBeEmpty();
        await expect(tabStrip).toBeVisible();
        const header = await topbar.boundingBox();
        const tabs = await tabStrip.boundingBox();
        await setConnectionState(page, 'WaitingForNetwork');
        await expect(activity(page)).toHaveText('Waiting for network…');
        expect(await topbar.boundingBox()).toEqual(header);
        expect(await tabStrip.boundingBox()).toEqual(tabs);
        await expect(downloads).toBeInViewport();
        await expect(edit).toBeInViewport();
        await expect(topbar.locator('.brand strong')).toHaveText('ChannelWave');
        const layout = await activity(page).locator('.telegram-activity-label').evaluate(el => ({ clipped: el.scrollWidth > el.clientWidth,
            overflow: document.documentElement.scrollWidth > innerWidth }));
        expect(layout).toEqual({ clipped: false, overflow: false });
        await page.screenshot({ path: info.outputPath(`home-header-${width}.png`) });
    }
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect(activity(page).locator('.loading-spinner')).toHaveCSS('animation-name', 'none');
    await edit.press('Enter');
    await expect(page.getByRole('heading', { name: 'Edit tabs', exact: true })).toBeVisible();
    await expect(edit).toHaveCount(0);
    await expect(activity(page)).toHaveText('Waiting for network…');
    await page.getByRole('button', { name: 'Done', exact: true }).click();
    await expect(tabStrip).toBeVisible();
    await expect(edit).toBeFocused();
    await downloads.click();
    await expect(page.getByRole('heading', { name: 'Downloads', exact: true })).toBeVisible();
    await expect(edit).toHaveCount(0);
    expect(pageErrors).toEqual([]);
});
