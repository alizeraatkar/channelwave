import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture } from './fixtures/telegram.mjs';

test('delayed navigation initialization preserves the first user destination', async ({ page }) => {
    await installTelegramFixture(page);
    let release, requested = false;
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/navigation.js', async route => {
        requested = true;
        await gate;
        await route.continue();
    });
    try {
        await page.goto('/#view=home');
        await page.locator('.app-shell').waitFor({ timeout: 60000 });
        await expect.poll(() => requested).toBe(true);
        await openRoot(page, 'Profile');
        const heading = page.getByRole('heading', { name: 'Connect Telegram', exact: true });
        await expect(heading).toBeVisible();
        release();
        await expect(page).toHaveURL(/#view=account$/);
        await expect(heading).toBeVisible();
    } finally { release(); }
});
