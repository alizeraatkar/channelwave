import { _android as android } from 'playwright';
import { expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

// The opt-in fixture has its own application ID and synthetic SQLite database.
const app = 'dev.channelwave.app.performance';
const output = process.argv[2] || 'artifacts/home-session/android';
const baseline = process.env.CHANNELWAVE_HOME_BASELINE === '1';
const runs = Number(process.env.CHANNELWAVE_PERF_RUNS || 5);
const serial = process.env.CHANNELWAVE_EMULATOR || 'emulator-5554';
if (!/^emulator-\d+$/.test(serial) || !Number.isInteger(runs) || runs < 1) throw new Error('Choose an emulator and a positive run count.');
const devices = await android.devices({ omitDriverInstall: true });
const device = devices.find(item => item.serial() === serial);
if (!device) { await Promise.all(devices.map(item => item.close())); throw new Error(`Missing emulator ${serial}`); }
const shell = async command => (await device.shell(command)).toString().trim();
const panel = page => page.locator('#home-tab-content');
let activePage;
const painted = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));

async function tab(page, name, count) {
    await expect(async () => {
        await page.getByRole('tab', { name, exact: true }).click();
        await expect(page.getByRole('tab', { name, exact: true })).toHaveAttribute('aria-selected', 'true', { timeout: 3000 });
    }).toPass({ timeout: 60000 });
    await expect(panel(page).locator('.virtual-tracks')).toHaveCount(1);
    await expect(panel(page).locator('.virtual-tracks')).toHaveAttribute('data-loaded-count', String(count), { timeout: 60000 });
    await painted(page);
}

async function timedNew(page) {
    await page.getByRole('tab', { name: 'New', exact: true }).evaluate(button => {
        globalThis.homePaint = null;
        const started = performance.now();
        const observer = new MutationObserver(check);
        function check() {
            if (button.getAttribute('aria-selected') !== 'true' || document.querySelector('#home-tab-content .virtual-tracks')?.dataset.loadedCount !== '100') return;
            observer.disconnect();
            requestAnimationFrame(() => requestAnimationFrame(() => { globalThis.homePaint = performance.now() - started; }));
        }
        observer.observe(document.querySelector('.music-home'), { attributes: true, childList: true, subtree: true });
        button.click(); check();
    });
    await page.waitForFunction(() => globalThis.homePaint !== null);
    return page.evaluate(() => homePaint);
}

try {
    await mkdir(output, { recursive: true });
    const component = (await shell(`cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER ${app}`)).split(/\r?\n/).at(-1);
    if (!/^dev\.channelwave\.app\.performance\/[\w.$]+$/.test(component)) throw new Error('Install the isolated performance fixture first.');
    const samples = [];
    for (const withMetadata of [false, true]) {
        await shell(`am force-stop ${app}`);
        await shell(`am start -W -n ${component} --ez channelwave.performance.metadata ${withMetadata}`);
        const view = await device.webView({ pkg: app }, { timeout: 150000 });
        const page = activePage = await view.page(); page.setDefaultTimeout(60000);
        await page.getByRole('tab', { name: 'New', exact: true }).waitFor();
        await tab(page, 'New', 100);
        await page.getByRole('button', { name: 'List view', exact: true }).click();
        const navigation = page.getByRole('navigation', { name: 'Mobile navigation' });
        for (let trial = 1; trial <= runs; trial++) {
            await tab(page, 'Liked', 0);
            const tabPaintMs = await timedNew(page);
            await page.locator('#main-content').evaluate(el => el.scrollTop = 1500);
            await painted(page);
            const previousTop = await page.locator('#main-content').evaluate(el => el.scrollTop);
            await navigation.getByRole('button', { name: 'Settings', exact: true }).click();
            await navigation.getByRole('button', { name: 'Home', exact: true }).click();
            await page.getByRole('tab', { selected: true }).waitFor();
            await painted(page);
            const selected = await page.getByRole('tab', { selected: true }).innerText();
            if (!baseline) {
                expect(selected).toBe('New');
                await expect.poll(() => page.locator('#main-content').evaluate(el => el.scrollTop)).toBeGreaterThan(previousTop - 80);
            }
            const restoredTop = await page.locator('#main-content').evaluate(el => el.scrollTop);
            samples.push({ withMetadata, trial, tabPaintMs, selected, previousTop, restoredTop });
            await writeFile(join(output, 'samples.json'), JSON.stringify(samples, null, 2));
            if (baseline) await tab(page, 'New', 100);
        }
        if (!baseline) {
            // Home key backgrounds the live activity. Screen sleep/wake must keep the same WebView state.
            await shell('input keyevent KEYCODE_HOME');
            await shell(`am start -W -n ${component}`);
            await expect(page.getByRole('tab', { name: 'New', exact: true })).toHaveAttribute('aria-selected', 'true');
            await shell('input keyevent KEYCODE_SLEEP');
            await shell('input keyevent KEYCODE_WAKEUP');
            await shell('wm dismiss-keyguard');
            await expect(page.getByRole('tab', { name: 'New', exact: true })).toHaveAttribute('aria-selected', 'true');
        }
        const pid = await shell(`pidof ${app}`);
        if (!/^\d+$/.test(pid)) throw new Error('Fixture process exited.');
        await writeFile(join(output, withMetadata ? 'metadata-managed.txt' : 'idle-managed.txt'), await shell(`logcat -d --pid=${pid} -v epoch -s ChannelWavePerf:I`));
        await page.screenshot({ path: join(output, withMetadata ? 'metadata.png' : 'idle.png') });
    }
    console.log(JSON.stringify({ baseline, samples }));
} catch (error) {
    if (activePage) {
        await writeFile(join(output, 'failure.html'), await activePage.content()).catch(() => {});
        await activePage.screenshot({ path: join(output, 'failure.png') }).catch(() => {});
    }
    await writeFile(join(output, 'failure-managed.txt'), await shell('logcat -d -v brief -s ChannelWavePerf:I chromium:E monodroid:E')).catch(() => {});
    throw error;
} finally {
    await shell(`am force-stop ${app}`);
    await Promise.all(devices.map(item => item.close()));
}
