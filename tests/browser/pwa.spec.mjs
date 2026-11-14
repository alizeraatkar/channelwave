import { test, expect } from '@playwright/test';

test('web app advertises a standalone install with full-size icons', async ({ page, browserName }) => {
    await page.goto('/');
    await expect(page.locator('link[rel="manifest"]')).toHaveAttribute('href', 'manifest.webmanifest');
    await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', '#121212');
    const manifest = await (await page.request.get('/manifest.webmanifest')).json();
    expect(manifest).toMatchObject({ id: '/', name: 'ChannelWave', start_url: '/', scope: '/', display: 'standalone' });
    expect(manifest.icons).toEqual(expect.arrayContaining([
        expect.objectContaining({ sizes: '192x192', purpose: 'any' }),
        expect.objectContaining({ sizes: '512x512', purpose: 'any' }),
        expect.objectContaining({ sizes: '512x512', purpose: 'maskable' })
    ]));
    for (const icon of manifest.icons) {
        const dimensions = await page.evaluate(async src => {
            const image = new Image(); image.src = src; await image.decode();
            return `${image.naturalWidth}x${image.naturalHeight}`;
        }, icon.src);
        expect(dimensions).toBe(icon.sizes);
    }
    if (browserName === 'chromium') {
        const client = await page.context().newCDPSession(page);
        const { errors } = await client.send('Page.getAppManifest');
        expect(errors).toEqual([]);
    }
});

test('published shell and install assets reopen offline', async ({ page, context, browserName }) => {
    test.skip(process.env.CHANNELWAVE_TEST_PUBLISHED !== '1', 'Requires the Release publish assets manifest.');
    await page.goto('/');
    await page.evaluate(() => window.channelwaveOfflineReady);
    await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller), { timeout: 30000 }).toBe(true);
    await expect(page.locator('.app-shell')).toBeVisible({ timeout: 30000 });
    if (browserName === 'chromium') {
        const client = await context.newCDPSession(page);
        const { installabilityErrors } = await client.send('Page.getInstallabilityErrors');
        // Playwright uses an isolated incognito context, where installation is disabled.
        expect(installabilityErrors.filter(error => error.errorId !== 'in-incognito')).toEqual([]);
    }
    await context.setOffline(true);
    await page.reload();
    await expect(page.locator('.app-shell')).toBeVisible({ timeout: 30000 });
    const cached = await page.evaluate(async () => {
        const manifest = await (await fetch('/manifest.webmanifest')).json();
        return await Promise.all(manifest.icons.map(async icon => (await fetch(icon.src)).ok));
    });
    expect(cached).toEqual([true, true, true]);
    await page.getByRole('button', { name: 'ChannelWave home', exact: true }).click();
    await expect(page.locator('.app-shell')).toBeVisible();
});
