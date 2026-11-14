import { test, expect } from '@playwright/test';
import { installTelegramFixture, connectFixture } from './fixtures/telegram.mjs';
import { securityHeaders } from '../../scripts/web-security.mjs';

test.beforeEach(() => test.skip(process.env.CHANNELWAVE_SECURITY_HEADERS !== '1', 'Requires serve-security-test.mjs with published assets.'));

test('published page and worker enforce the production policy and block injected scripts', async ({ page }) => {
    for (const file of ['/', '/audio-worker.js', '/tdweb/tdweb.worker.js']) {
        const response = await page.request.get(file);
        expect(response.ok()).toBe(true);
        for (const [name, value] of Object.entries(securityHeaders)) expect(response.headers()[name.toLowerCase()]).toBe(value);
    }
    await page.goto('/');
    await expect(page.locator('.app-shell')).toBeVisible();
    const result = await page.evaluate(async () => {
        globalThis.securityInjected = false;
        const script = document.createElement('script');
        script.textContent = 'globalThis.securityInjected = true';
        document.head.append(script);
        await new Promise(resolve => setTimeout(resolve, 50));
        script.remove();
        return securityInjected;
    });
    expect(result).toBe(false);
});

test('an unrelated origin cannot frame the application', async ({ page, baseURL }) => {
    // Both origins are loopback, avoiding browser local-network permission blocking
    // before the app's actual framing policy can be evaluated.
    const parent = new URL('/__security_parent', baseURL); parent.hostname = 'localhost';
    const blocked = page.waitForEvent('requestfailed', { predicate: request => request.url() === `${baseURL}/` && request.resourceType() === 'document' });
    await page.goto(parent.href);
    await expect(page.frameLocator('#control').getByText('frame control', { exact: true })).toBeVisible();
    expect((await blocked).failure()?.errorText).toMatch(/ERR_BLOCKED_BY_RESPONSE|Content Security Policy/i);
    expect(await page.frameLocator('#app').locator('.app-shell').count()).toBe(0);
});

test('synthetic Telegram login still works with enforced CSP', async ({ page }) => {
    await installTelegramFixture(page);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto('/');
    await connectFixture(page);
    await expect(page.getByRole('button', { name: 'Add to library: :)', exact: true })).toBeVisible();
    expect(errors).toEqual([]);
});
