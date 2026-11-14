import { _android } from 'playwright';
import { expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const app = 'dev.channelwave.app.performance';
const output = process.argv[2] || 'artifacts/android-browsing-performance';
const runs = Number(process.env.CHANNELWAVE_PERF_RUNS || 5);
const baseline = process.env.CHANNELWAVE_PERF_BASELINE === '1';
const condition = process.env.CHANNELWAVE_PERF_CONDITION || 'both';
if (!['both', 'idle', 'metadata'].includes(condition)) throw new Error('Invalid metadata condition.');
const soakMs = Number(process.env.CHANNELWAVE_SOAK_SECONDS || 0) * 1000;
const serial = process.env.CHANNELWAVE_EMULATOR || 'emulator-5554';
if (!Number.isInteger(runs) || runs < 1 || !Number.isFinite(soakMs) || soakMs < 0) throw new Error('Invalid run count or soak duration.');
if (!/^emulator-\d+$/.test(serial)) throw new Error('Select an Android emulator.');
const devices = await _android.devices({ omitDriverInstall: true });
const device = devices.find(d => d.serial() === serial);
if (!device) { await Promise.all(devices.map(d => d.close())); throw new Error('Android emulator unavailable.'); }
const shell = async command => (await device.shell(command)).toString().trim();
const paint = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
async function settledScroll() {
    await page.evaluate(() => { window.scrollSettling = { top: document.getElementById('main-content').scrollTop, since: performance.now() }; });
    await page.waitForFunction(() => {
        const top = document.getElementById('main-content').scrollTop;
        if (Math.abs(top - window.scrollSettling.top) > .5) window.scrollSettling = { top, since: performance.now() };
        return performance.now() - window.scrollSettling.since >= 180;
    }, undefined, { timeout: 15000 });
}
const percentile = values => values.length ? [...values].sort((a, b) => a - b)[Math.ceil(values.length * .95) - 1] : null;
let page;
const samples = [];
const errors = [];

async function homeTab(name) {
    await page.getByRole('tab', { name, exact: true }).click();
    await expect(page.getByRole('tab', { name, exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('#home-tab-content .virtual-tracks')).toHaveCount(1);
    if (name === 'New') await expect.poll(async () => Number(await page.locator('#home-tab-content .virtual-tracks').getAttribute('data-loaded-count'))).toBeGreaterThanOrEqual(100);
    else await expect(page.locator('#home-tab-content .virtual-tracks')).toHaveAttribute('data-loaded-count', '0');
    await expect(page.locator('#main-content')).not.toHaveAttribute('data-home-restoring', 'true');
    await paint(page);
}

async function timedClick(locator, selector) {
    await locator.evaluate((button, selector) => {
        window.browsingPaint = null;
        const started = performance.now();
        const observer = new MutationObserver(check);
        function check() {
            const target = document.querySelector(selector);
            if (!target?.getClientRects().length) return;
            observer.disconnect();
            requestAnimationFrame(() => requestAnimationFrame(() => { window.browsingPaint = performance.now() - started; }));
        }
        observer.observe(document.getElementById('main-content'), { childList: true, subtree: true, attributes: true });
        if (selector === '.settings-row') {
            const root = document.getElementById('main-content');
            const bounds = root.getBoundingClientRect();
            const row = [...root.querySelectorAll('[data-track-key]')].find(row => row.getBoundingClientRect().top >= bounds.top);
            window.navigationSnapshot = { top: root.scrollTop, key: row?.dataset.trackKey, rowTop: row?.getBoundingClientRect().top };
        }
        button.click(); check();
    }, selector);
    await page.waitForFunction(() => window.browsingPaint !== null);
    return page.evaluate(() => window.browsingPaint);
}

async function gesture(label) {
    const cdp = await page.context().newCDPSession(page);
    const trace = [];
    cdp.on('Tracing.dataCollected', data => trace.push(...data.value));
    await cdp.send('Tracing.start', { categories: 'devtools.timeline,disabled-by-default-devtools.timeline,blink.user_timing', transferMode: 'ReportEvents' });
    await shell(`dumpsys gfxinfo ${app} reset`);
    await page.evaluate(() => { window.browsingHistoryWrites = 0; });
    const box = await page.locator('#main-content').boundingBox();
    const dimensions = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
    const screen = await shell('wm size');
    const match = screen.match(/(?:Override|Physical) size: (\d+)x(\d+)/);
    if (!match) throw new Error(screen);
    const x = Math.round((box.x + box.width / 2) / dimensions.width * Number(match[1]));
    const y = fraction => Math.round((box.y + box.height * fraction) / dimensions.height * Number(match[2]));
    for (let i = 0; i < 4; i++) await shell(`input swipe ${x} ${y(.8)} ${x} ${y(.25)} 400`);
    await paint(page);
    const historyWrites = await page.evaluate(() => window.browsingHistoryWrites);
    const raw = await shell(`dumpsys gfxinfo ${app} framestats`);
    const overrun = raw.split('---PROFILEDATA---').filter(s => s.trimStart().startsWith('Flags,')).flatMap(section => {
        const [head, ...rows] = section.trim().split(/\r?\n/);
        const columns = head.split(',');
        const deadline = columns.indexOf('FrameDeadline'), end = columns.indexOf('FrameCompleted');
        return rows.map(row => row.split(',').map(Number)).filter(row => row[0] === 0 && row[deadline] > 0 && row[end] > 0 && row[end] < Number.MAX_SAFE_INTEGER).map(row => (row[end] - row[deadline]) / 1e6);
    });
    if (!overrun.length) throw new Error('Android returned no completed frame samples.');
    let traceTimer;
    const complete = new Promise((resolve, reject) => {
        cdp.once('Tracing.tracingComplete', resolve);
        traceTimer = setTimeout(() => reject(new Error('WebView tracing did not finish.')), 15000);
    });
    try { await Promise.all([cdp.send('Tracing.end'), complete]); }
    finally { clearTimeout(traceTimer); await cdp.detach(); }
    await writeFile(join(output, `${label}-frames.txt`), raw);
    await writeFile(join(output, `${label}-webview.json`), JSON.stringify({ traceEvents: trace }));
    const tasks = trace.filter(e => e.ph === 'X' && e.name === 'RunTask').map(e => e.dur / 1000);
    return { frames: overrun.length, missedDeadlines: overrun.filter(n => n > 0).length, p95OverrunMs: percentile(overrun), p95TaskMs: percentile(tasks), historyWrites };
}

try {
    await mkdir(output, { recursive: true });
    await writeFile(join(output, 'environment.txt'), [await shell('getprop ro.build.fingerprint'), await shell('wm size'),
        await shell('dumpsys webviewupdate'), await shell(`dumpsys package ${app}`)].join('\n'));
    await shell('input keyevent KEYCODE_WAKEUP'); await shell('wm dismiss-keyguard');
    const component = (await shell(`cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER ${app}`)).split(/\r?\n/).at(-1);
    if (!component.startsWith(app + '/')) throw new Error('Install the isolated fixture APK first.');
    for (const metadata of condition === 'both' ? [false, true] : [condition === 'metadata']) {
        await shell(`am force-stop ${app}`);
        const startupStarted = Date.now();
        await shell(`am start -n ${component} --ez channelwave.performance.metadata ${metadata}`);
        let processPid;
        const processDeadline = Date.now() + 15000;
        do {
            processPid = await shell(`pidof ${app}`);
            if (/^\d+$/.test(processPid)) break;
            await new Promise(resolve => setTimeout(resolve, 100));
        } while (Date.now() < processDeadline);
        if (!/^\d+$/.test(processPid)) throw new Error('Fixture process did not start.');
        // The device can briefly retain the previous process's WebView after
        // force-stop. Attach to the fresh PID rather than its cached package.
        const matches = view => view.pkg() === app && view.pid() === Number(processPid);
        const view = device.webViews().find(matches) || await device.waitForEvent('webview', { predicate: matches, timeout: 150000 });
        page = await view.page(); page.setDefaultTimeout(60000);
        page.on('pageerror', e => errors.push(e.message));
        page.on('console', message => { if (message.type() === 'error' && /disposed object|Unhandled|One or more errors occurred/i.test(message.text())) errors.push(message.text()); });
        page.on('console', message => { if (!baseline && /Throttling navigation/.test(message.text())) errors.push(message.text()); });
        await page.getByRole('tab', { name: 'New', exact: true }).waitFor();
        await page.locator('.home-track').first().waitFor();
        await page.evaluate(() => {
            const replace = history.replaceState.bind(history);
            history.replaceState = (...args) => { window.browsingHistoryWrites = (window.browsingHistoryWrites || 0) + 1; return replace(...args); };
        });
        const nav = page.getByRole('navigation', { name: 'Mobile navigation' });
        await homeTab('New');
        const startupReadyMs = Date.now() - startupStarted;
        const firstSettingsMs = await timedClick(nav.getByRole('button', { name: 'Settings', exact: true }), '.settings-row');
        const firstHomeMs = await timedClick(nav.getByRole('button', { name: 'Home', exact: true }), '#home-tab-content .virtual-tracks');
        for (const mode of ['List', 'Grid']) {
            await page.getByRole('button', { name: `${mode} view`, exact: true }).click();
            await settledScroll();
            await nav.getByRole('button', { name: 'Settings', exact: true }).click();
            await nav.getByRole('button', { name: 'Home', exact: true }).click();
            await homeTab('New');
        }
        await nav.getByRole('button', { name: 'Library', exact: true }).click();
        await page.locator('.library-landing .library-tiles').first().getByRole('button', { name: /^Tracks(?: |$)/ }).click();
        await expect(page.locator('.virtual-tracks').filter({ visible: true })).toHaveAttribute('data-loaded-count', '100');
        await shell('input keyevent KEYCODE_BACK');
        await expect(page.locator('.library-landing')).toBeVisible();
        await nav.getByRole('button', { name: 'Home', exact: true }).click();
        await writeFile(join(output, `${metadata ? 'metadata' : 'idle'}-startup.json`), JSON.stringify({ startupReadyMs, firstSettingsMs, firstHomeMs }));
        for (let trial = 1; trial <= runs; trial++) {
            await homeTab('New');
            for (const mode of ['List', 'Grid']) {
                await page.getByRole('button', { name: `${mode} view`, exact: true }).click();
                const viewport = page.locator('#home-tab-content .virtual-tracks');
                if (mode === 'Grid') await expect(viewport).toHaveClass(/virtual-grid/);
                else await expect(viewport).not.toHaveClass(/virtual-grid/);
                await paint(page);
                await expect(page.locator('#main-content')).not.toHaveAttribute('data-home-restoring', 'true');
                await page.locator('#main-content').evaluate(el => el.scrollTop = 0);
                await settledScroll();
                const scroll = await gesture(`${metadata ? 'metadata' : 'idle'}-${trial}-${mode}`);
                await settledScroll();
                const settingsMs = await timedClick(nav.getByRole('button', { name: 'Settings', exact: true }), '.settings-row');
                const snapshot = await page.evaluate(() => window.navigationSnapshot);
                const homeRestoreStarted = Date.now();
                const homeMs = await timedClick(nav.getByRole('button', { name: 'Home', exact: true }), '#home-tab-content .virtual-tracks');
                await expect(page.locator('#blazor-error-ui')).toBeHidden();
                await expect(page.locator('#main-content')).not.toHaveAttribute('data-home-restoring', 'true');
                await settledScroll();
                await writeFile(join(output, 'restoration-pending.json'), JSON.stringify({ metadata, trial, mode, snapshot }));
                let restoredCorrectly = true;
                try { await expect.poll(() => page.locator('#main-content').evaluate((el, top) => Math.abs(el.scrollTop - top), snapshot.top)).toBeLessThan(80); }
                catch (error) { restoredCorrectly = false; if (!baseline) throw error; }
                const restoredTop = await page.locator('#main-content').evaluate(el => el.scrollTop);
                const homeRestoredMs = Date.now() - homeRestoreStarted;
                samples.push({ metadata, trial, mode, scroll, settingsMs, homeMs, homeRestoredMs, snapshot, restoredTop, restoredCorrectly });
                await writeFile(join(output, 'samples.json'), JSON.stringify({ samples, errors }, null, 2));
                console.log(JSON.stringify(samples.at(-1)));
            }
            await homeTab('Liked'); await homeTab('New');
            await nav.getByRole('button', { name: 'Library', exact: true }).click();
            await page.locator('.library-landing .library-tiles').first().getByRole('button', { name: /^Tracks(?: |$)/ }).click();
            await expect(page.locator('.virtual-tracks').filter({ visible: true })).toHaveAttribute('data-loaded-count', '100');
            const libraryScroll = await gesture(`${metadata ? 'metadata' : 'idle'}-${trial}-Tracks`);
            await shell('input keyevent KEYCODE_BACK');
            let backRestored = true;
            try { await expect(page.locator('.library-landing')).toBeVisible(); }
            catch (error) { backRestored = false; if (!baseline) throw error; }
            // A failing baseline still records subsequent scenarios. This direct
            // click recovers the test UI; it does not count as a passing Back.
            if (backRestored) await nav.getByRole('button', { name: 'Home', exact: true }).click();
            else await page.getByRole('button', { name: 'ChannelWave home', exact: true }).click();
            samples.push({ metadata, trial, mode: 'Tracks', scroll: libraryScroll, backRestored });
            await writeFile(join(output, 'samples.json'), JSON.stringify({ samples, errors }, null, 2));
        }
        const trialPid = await shell(`pidof ${app}`);
        if (!/^\d+$/.test(trialPid)) throw new Error('Fixture process exited during trials.');
        const trialManaged = await shell(`logcat -d --pid=${trialPid} -v epoch -s ChannelWavePerf:I chromium:E monodroid:E`);
        await writeFile(join(output, `${metadata ? 'metadata' : 'idle'}-trials-managed.txt`), trialManaged);
        if (metadata && !trialManaged.includes('Refresh after background changes')) throw new Error('Metadata trials did not observe a background refresh.');
        const soakStarted = Date.now();
        const until = soakStarted + (metadata ? soakMs : 0);
        let cycles = 0;
        while (Date.now() < until) {
            await homeTab('New');
            await shell('input swipe 540 1600 540 600 300');
            await settledScroll();
            const top = await page.locator('#main-content').evaluate(el => el.scrollTop);
            await nav.getByRole('button', { name: 'Settings', exact: true }).click();
            await nav.getByRole('button', { name: 'Home', exact: true }).click();
            await expect(page.locator('#home-tab-content .virtual-tracks')).toBeVisible();
            await expect(page.locator('#main-content')).not.toHaveAttribute('data-home-restoring', 'true');
            await settledScroll();
            await expect.poll(() => page.locator('#main-content').evaluate((el, top) => Math.abs(el.scrollTop - top), top)).toBeLessThan(80);
            expect(await page.locator('#home-tab-content [data-track-key]').count()).toBeLessThan(61);
            await expect(page.locator('#blazor-error-ui')).toBeHidden();
            await homeTab('Liked'); cycles++;
            if (cycles % 10 === 0) {
                const progress = { cycles, elapsedMs: Date.now() - soakStarted, requestedDurationMs: soakMs, errors };
                await writeFile(join(output, 'soak-progress.json'), JSON.stringify(progress));
                console.log(JSON.stringify({ soak: progress }));
            }
        }
        await shell('input keyevent KEYCODE_HOME'); await shell(`am start -n ${component}`);
        await expect(page.locator('.music-home')).toBeVisible();
        await shell('input keyevent KEYCODE_SLEEP'); await shell('input keyevent KEYCODE_WAKEUP'); await shell('wm dismiss-keyguard');
        await expect(page.locator('.music-home')).toBeVisible();
        const pid = await shell(`pidof ${app}`);
        if (pid !== trialPid) throw new Error('Fixture process changed during lifecycle/soak checks.');
        const managed = trialManaged + '\n' + await shell(`logcat -d --pid=${pid} -v epoch -s ChannelWavePerf:I chromium:E monodroid:E`);
        await writeFile(join(output, `${metadata ? 'metadata' : 'idle'}-managed.txt`), managed);
        if (metadata && !managed.includes('Refresh after background changes')) throw new Error('Metadata-enabled run did not observe a background refresh.');
        await page.screenshot({ path: join(output, `${metadata ? 'metadata' : 'idle'}.png`) });
        await writeFile(join(output, `${metadata ? 'metadata' : 'idle'}-soak.json`), JSON.stringify({ cycles, durationMs: Date.now() - soakStarted, requestedDurationMs: metadata ? soakMs : 0, errors }));
    }
    expect(errors).toEqual([]);
} catch (error) {
    await writeFile(join(output, 'failure.txt'), error.stack || String(error));
    if (page) await page.screenshot({ path: join(output, 'failure.png') }).catch(() => {});
    throw error;
} finally {
    await shell(`am force-stop ${app}`).catch(() => {});
    await Promise.all(devices.map(d => d.close()));
}
