import { expect } from '@playwright/test';

// Loaded collection size is independent of the bounded set of mounted rows.
export async function expectLoadedTracks(page, count) {
    await expect(page.locator('.music-tracks')).toHaveAttribute('data-loaded-count', String(count));
}
