import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { seedPerformance } from './fixtures/performance.mjs';
import { openRoot } from './fixtures/telegram.mjs';

test.beforeEach(async ({ page }) => {
    const source = await readFile('src/ChannelWave.UI/wwwroot/navigation.js', 'utf8');
    await page.route('**/navigation-under-test.js', route => route.fulfill({ contentType: 'text/javascript', body: source }));
    await page.route('**/scroll-harness', route => route.fulfill({ contentType: 'text/html', body: `
        <main id="main-content" tabindex="-1" style="height:500px;overflow:auto">
          <section class="music-home"><div data-loaded-count="300" style="height:6000px"><button id="song">Song</button></div></section>
        </main>` }));
    await page.goto('/scroll-harness');
    await page.evaluate(async () => {
        window.nav = await import('/navigation-under-test.js');
        window.saves = []; window.routes = []; window.writes = 0;
        const replace = history.replaceState.bind(history);
        history.replaceState = (...args) => { writes++; return replace(...args); };
        nav.watchNavigation({ invokeMethodAsync: async (method, route) => { routes.push(route); nav.recordPage(route); nav.focusPage(); } }, 'home');
        nav.recordPage('home'); nav.focusPage();
        window.watchHome = (account = 700, view = 'new', top = 0) => nav.watchHomeScroll({
            invokeMethodAsync: async (method, account, view, top) => { saves.push({ account, view, top }); }
        }, account, view, top);
        window.watcher = watchHome();
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        writes = 0;
    });
});

test('continuous scrolling defers history and managed persistence until idle', async ({ page }) => {
    await page.evaluate(async () => {
        const main = document.getElementById('main-content');
        for (let i = 1; i <= 18; i++) {
            main.scrollTop = i * 50; main.dispatchEvent(new Event('scroll'));
            await new Promise(resolve => requestAnimationFrame(resolve));
        }
        window.duringScroll = { writes, saves: saves.length };
    });
    expect(await page.evaluate(() => duringScroll)).toEqual({ writes: 0, saves: 0 });
    await expect.poll(() => page.evaluate(() => saves.at(-1)?.top)).toBe(900);
    expect(await page.evaluate(() => saves.length)).toBe(1);
    expect(await page.evaluate(() => history.state.channelwave.scroll)).toBe(900);
});

test('immediate navigation captures the last position and Back restores it', async ({ page }) => {
    await page.evaluate(async () => {
        const main = document.getElementById('main-content');
        main.scrollTop = 1234; main.dispatchEvent(new Event('scroll'));
        await nav.captureHomeScroll(); nav.capturePage(); await watcher.dispose(); nav.recordPage('settings');
        main.scrollTop = 0;
        nav.goBack();
    });
    await expect.poll(() => page.evaluate(() => routes.at(-1))).toBe('home');
    await expect.poll(() => page.locator('#main-content').evaluate(el => el.scrollTop)).toBe(1234);
    expect(await page.evaluate(() => saves.at(-1))).toEqual({ account: 700, view: 'new', top: 1234 });
    await page.waitForTimeout(250);
    expect(await page.evaluate(() => history.state.channelwave.loaded)).toBe(300);
});

test('rapid view disposal flushes cached positions without reading the next view', async ({ page }) => {
    await page.evaluate(async () => {
        const main = document.getElementById('main-content');
        main.scrollTop = 800; main.dispatchEvent(new Event('scroll'));
        main.scrollTop = 0; // The next tab has already rendered when disposal runs.
        await watcher.dispose();
        window.watcher = watchHome(701, 'liked', 0);
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        main.scrollTop = 400; main.dispatchEvent(new Event('scroll'));
        await watcher.dispose();
        nav.stopWatchingNavigation();
    });
    const saved = await page.evaluate(() => saves);
    expect(saved).toEqual([{ account: 700, view: 'new', top: 800 }, { account: 701, view: 'liked', top: 400 }]);
    await page.waitForTimeout(300);
    expect(await page.evaluate(() => saves)).toEqual(saved);
});

test('backgrounding flushes pending Home and history state', async ({ page }) => {
    await page.evaluate(() => {
        const main = document.getElementById('main-content');
        main.scrollTop = 650; main.dispatchEvent(new Event('scroll'));
        window.dispatchEvent(new Event('pagehide'));
    });
    await expect.poll(() => page.evaluate(() => saves.at(-1)?.top)).toBe(650);
    expect(await page.evaluate(() => history.state.channelwave.scroll)).toBe(650);
});

test('scroll clamping after Home is removed cannot overwrite its captured position', async ({ page }) => {
    await page.evaluate(async () => {
        const main = document.getElementById('main-content');
        main.scrollTop = 1800; main.dispatchEvent(new Event('scroll'));
        await nav.captureHomeScroll();
        main.innerHTML = '<section style="height:500px">Settings</section>';
        main.dispatchEvent(new Event('scroll'));
        await watcher.dispose();
    });
    expect(await page.evaluate(() => saves)).toEqual([{ account: 700, view: 'new', top: 1800 }]);
});

test('a viewport settles only after its requested managed window has rendered', async ({ page }) => {
    const source = await readFile('src/ChannelWave.UI/wwwroot/track-viewport.js', 'utf8');
    await page.route('**/viewport-under-test.js', route => route.fulfill({ contentType: 'text/javascript', body: source }));
    await page.evaluate(async () => {
        const main = document.getElementById('main-content');
        main.innerHTML = '<div id="tracks" class="virtual-tracks" data-window-revision="0"><div data-track-key="700:0" style="height:124px">Song</div></div>';
        window.viewportModule = await import('/viewport-under-test.js');
        window.windowsRequested = 0; window.viewportSettled = 0;
        main.addEventListener('channelwaveviewportsettled', () => window.viewportSettled++);
        const element = document.getElementById('tracks');
        viewportModule.watchViewport(element, { invokeMethodAsync: async method => { if (method === 'WindowChangedAsync') window.windowsRequested++; } });
        viewportModule.configureViewport(element, Array.from({ length: 100 }, (_, i) => `700:${i}`), 1, true, false, false, false);
        for (let i = 0; i < 5; i++) await new Promise(resolve => requestAnimationFrame(resolve));
    });
    expect(await page.evaluate(() => windowsRequested)).toBeGreaterThan(0);
    expect(await page.evaluate(() => viewportSettled)).toBe(0);
    await page.evaluate(() => {
        const element = document.getElementById('tracks');
        element.dataset.windowRevision = String(windowsRequested);
        viewportModule.refreshViewport(element);
    });
    await expect.poll(() => page.evaluate(() => viewportSettled)).toBeGreaterThan(0);
    await page.evaluate(() => viewportModule.unwatchViewport(document.getElementById('tracks')));
});

test('Home preserves the latest position when leaving before the scroll event is delivered', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seedPerformance(page, 500);
    await openRoot(page, 'Home');
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    await expect(page.locator('#main-content')).not.toHaveAttribute('data-home-restoring', 'true');
    await page.getByRole('navigation', { name: 'Mobile navigation' }).getByRole('button', { name: 'Settings', exact: true }).evaluate(button => {
        document.getElementById('main-content').scrollTop = 1450;
        button.click();
    });
    await expect(page.locator('.music-home')).toHaveCount(0);
    await openRoot(page, 'Home');
    await expect.poll(() => page.locator('#main-content').evaluate(el => el.scrollTop)).toBeGreaterThan(1370);
    await page.getByRole('tab', { name: 'Liked', exact: true }).click();
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await expect.poll(() => page.locator('#main-content').evaluate(el => el.scrollTop)).toBeGreaterThan(1370);
});

test('leaving Home while observer binding is pending does not dispose an in-use JS reference', async ({ page }) => {
    const source = await readFile('src/ChannelWave.UI/wwwroot/navigation.js', 'utf8');
    await page.route('**/_content/ChannelWave.UI/navigation.js', route => route.fulfill({ contentType: 'text/javascript', body:
        source.replace('export function watchHomeScroll(', 'function createHomeScroll(') + `
        export async function watchHomeScroll(...args) {
            if (globalThis.delayHomeBinding) {
                globalThis.homeBindingStarted = true;
                await new Promise(resolve => setTimeout(resolve, 500));
            }
            return createHomeScroll(...args);
        }` }));
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    await page.setViewportSize({ width: 390, height: 844 });
    await seedPerformance(page, 500);
    await openRoot(page, 'Home');
    await expect(page.locator('#main-content')).not.toHaveAttribute('data-home-restoring', 'true');
    await page.evaluate(() => { window.delayHomeBinding = true; });
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await page.waitForFunction(() => window.homeBindingStarted);
    await openRoot(page, 'Settings');
    await openRoot(page, 'Home');
    await page.evaluate(() => { window.delayHomeBinding = false; });
    await page.getByRole('tab', { name: 'Liked', exact: true }).click();
    await page.getByRole('tab', { name: 'New', exact: true }).click();
    await expect(page.locator('#home-tab-content .virtual-tracks')).toHaveAttribute('data-loaded-count', '100');
    await expect(page.locator('#main-content')).not.toHaveAttribute('data-home-restoring', 'true');
    await expect(page.locator('#blazor-error-ui')).toBeHidden();
    expect(errors).toEqual([]);
});
