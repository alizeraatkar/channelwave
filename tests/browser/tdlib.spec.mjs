import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';

test('real pinned TDLib initializes in a worker without account credentials or external networking', async ({ page, context, baseURL }) => {
    test.skip(process.env.CHANNELWAVE_VERIFY_TDWEB !== '1', 'Build and audit the pinned tdweb assets, then set CHANNELWAVE_VERIFY_TDWEB=1. This is an explicit integration gate.');
    test.setTimeout(120000);
    const origin = new URL(baseURL).origin;
    await context.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    await context.routeWebSocket('**/*', socket => socket.close());
    let workerCreated = false;
    page.on('worker', () => { workerCreated = true; });
    await page.goto('/');
    await openRoot(page, 'Profile');
    await page.getByText('Connection details', { exact: true }).click();
    await page.getByRole('button', { name: 'Check Telegram engine' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'TDLib 1.8.68; pinned commit verified' })).toBeVisible({ timeout: 90000 });
    expect(workerCreated).toBe(true);
    await expect(page.getByRole('alert')).toHaveCount(0);
});

test('real pinned engine initializes its profile and closes without signing into an account', async ({ page, context, baseURL }) => {
    test.skip(process.env.CHANNELWAVE_VERIFY_TDWEB !== '1', 'Requires the rebuilt audited engine.');
    test.setTimeout(120000);
    const origin = new URL(baseURL).origin;
    await context.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    await context.routeWebSocket('**/*', socket => socket.close());
    await page.route('**/channelwave.local.json', route => route.fulfill({ json: { apiId: 1, apiHash: '0'.repeat(32) } }));
    await page.goto('/');
    await page.getByRole('button', { name: 'Connect account', exact: true }).click();
    await expect(page.getByLabel('Phone number with country code', { exact: true })).toBeVisible({ timeout: 90000 });
    // No phone, code or password is supplied. Destroy only the synthetic empty profile.
    await page.evaluate(async () => {
        const bridge = await import('/_content/ChannelWave.Platform.Browser/channelwave.js');
        await bridge.sendTelegram(JSON.stringify({ '@type': 'destroy' }));
    });
    await expect(page.getByText('Session closed. Reopen to connect.', { exact: true })).toBeVisible();
});
